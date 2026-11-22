#!/usr/bin/env python3
"""Run portable LAN instances with independent saves and verify room/race progress.

Run inside one host/network namespace with a working SDL display backend.
A license in slot zero must already exist in each product's private NAND.
"""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import socket
import struct
import subprocess
import time


def write_pad(path, buttons=0, x=0, y=0):
    temporary = path.with_suffix('.pending')
    temporary.write_bytes(struct.pack('>Hbb', buttons, x, y))
    os.replace(temporary, path)


def decode_observation(packet, peer, ids):
    if len(packet) != 28:
        return None
    words = struct.unpack('>7I', packet)
    magic, instance = words[:2]
    if magic not in (0x4d4b574c, 0x4d4b5752, 0x4d4b574e) or instance not in ids:
        return None
    if peer[0] != f'127.0.0.{instance}':
        raise RuntimeError(f'Instance {instance} used unexpected source address {peer}')
    return words



def finished_progress(line, count):
    """Accept actual simulation finish evidence, never menu text or room counts."""
    if any(marker in line for marker in (b'FATAL', b'[runtime] fatal log',
            b'scheduler could not switch away', b'WARNING unmapped guest touch')):
        raise RuntimeError(f'Guest fault during race: {line.decode(errors="replace").strip()}')
    if b'[race-progress]' not in line:
        return None
    record = {key.decode(): int(value) for key, value in re.findall(rb'(\w+)=(\d+)', line)}
    required = {'stage', 'frames', 'players', 'capacity', 'observed', 'course', 'mode', 'finished'}
    if not required <= record.keys():
        return None
    if (record['stage'] < 3 or record['frames'] < 120 or record['players'] != count or
            record['observed'] != count or record['finished'] != count or record['capacity'] < count):
        return None
    return record


def sdk_ready(network, ids, host_id, count, now, timeout):
    return all(i in network and 0 <= now-network[i][1] < timeout and
        network[i][0]['players'] == count and not network[i][0]['suspended'] and
        network[i][0]['connected'].bit_count() == count and
        network[i][0]['connected'] == network[host_id][0]['connected'] and
        network[i][0]['role'] == (2 if i == host_id else 1) and
        network[i][0]['match'] == (12 if i == host_id else 1) for i in ids)


def run(args):
    test_clock = int(os.environ.get('MKW_TEST_CLOCK_DIVISOR', '1'))
    if not 1 <= test_clock <= 64:
        raise ValueError('Test clock divisor must be between one and 64')
    if args.visible and test_clock != 1:
        raise ValueError('--visible requires the normal test clock')
    if test_clock != 1:
        print(json.dumps(dict(test_clock_divisor=test_clock,
            validation='functional only; not default-timing or real-time performance')), flush=True)
    directories = [args.host.resolve()] + [path.resolve() for path in args.client]
    available = [i for i in range(1, args.capacity+1) if i != args.host_id]
    client_ids = args.client_id if args.client_id is not None else available[:len(args.client)]
    ids = [args.host_id] + client_ids
    if len(ids) != len(directories) or len(ids) > args.capacity:
        raise ValueError('Supply one client ID per directory, within the requested build capacity')
    if any(not 1 <= instance <= args.capacity for instance in ids):
        raise ValueError('Runtime IDs must fit the requested build capacity')
    if len(set(ids)) != len(ids):
        raise ValueError('Local instances require different runtime IDs')
    if not 3 <= args.telemetry_timeout <= 120:
        raise ValueError('Telemetry timeout must be between three and 120 seconds')
    if not 0 <= args.startup_gap <= 30:
        raise ValueError('Startup gap must be between zero and thirty seconds')
    if not 0.05 <= args.menu_pulse <= 3:
        raise ValueError('Menu pulse must be between 0.05 and three seconds')
    count = len(ids)
    binary = 'WiiCompiledLAN.exe' if os.name == 'nt' else 'WiiCompiledLAN'
    hashes = []
    for directory in directories:
        digest = hashlib.sha256()
        with (directory / binary).open('rb') as executable:
            for chunk in iter(lambda: executable.read(256*1024), b''):
                digest.update(chunk)
        hashes.append(digest.digest())
    if any(value != hashes[0] for value in hashes[1:]):
        raise ValueError('Use the same unified LAN executable in every portable directory')
    if len(set(directories)) != len(directories):
        raise ValueError('Every instance requires a different portable directory')
    processes, logs, pads = [], [], []
    log_positions, log_pending, finished = {}, {}, {}
    next_finish_check = 0
    finish_since = None
    status, race, network, last_input = {}, {}, {}, {}
    room_reported = False
    race_reported = False
    room_confirmed = race_since = None
    race_start_frames = {}
    started = time.monotonic()
    ready_since = None
    next_summary = started + 30
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as monitor:
        # Bind before launching either process. Failure means another test owns it.
        monitor.bind(('127.0.0.254', 27900))
        monitor.settimeout(0.2)
        try:
            for index, (directory, instance, role) in enumerate(zip(directories, ids, ["--lan-host"] + ["--lan-join"] * (count-1))):
                if index and args.startup_gap:
                    time.sleep(args.startup_gap)
                executable = directory / ('WiiCompiledLAN.exe' if os.name == 'nt' else 'WiiCompiledLAN')
                if not executable.is_file() or not (directory / 'portable.txt').is_file():
                    raise ValueError(f'Expected a portable LAN product in {directory}')
                pad = directory / 'lan-test-pad.bin'
                write_pad(pad)
                pads.append(pad)
                log = (directory / 'lan-test-game.log').open('w')
                logs.append(log)
                environment = dict(os.environ, WIICOMPILED_TEST_PAD_FILE=str(pad))
                if args.finish:
                    environment['MKW_TEST_LAN_CPU'] = '1'
                command = [str(executable), '--lan-local', f'--lan-id={instance}', f'--lan-peers={args.capacity}', role]
                if not args.visible:
                    command.append('--headless')
                processes.append(subprocess.Popen(command, cwd=directory,
                    env=environment, stdout=log, stderr=subprocess.STDOUT))
                if args.sequential_join or (args.host_first and index == 0):
                    launched = ids[:index+1]
                    while time.monotonic() - started < args.timeout:
                        for launched_id, process in zip(launched, processes):
                            if process.poll() is not None:
                                raise RuntimeError(f'Instance {launched_id} exited with {process.returncode}')
                        try:
                            packet, peer = monitor.recvfrom(4096)
                        except socket.timeout:
                            continue
                        observation = decode_observation(packet, peer, launched)
                        if observation is None or observation[0] != 0x4d4b574c:
                            continue
                        _, observed, state, players, section, page, transition = observation
                        now = time.monotonic()
                        record = dict(state=state, players=players, section=section, page=page, transition=transition)
                        if status.get(observed, (None,))[0] != record:
                            print(json.dumps(dict(elapsed=round(now-started, 2), instance=observed, **record)), flush=True)
                        status[observed] = record, now
                        if all(i in status and now-status[i][1] < args.telemetry_timeout and
                               status[i][0]['state'] == (3 if i == args.host_id else 4) and
                               status[i][0]['players'] == len(launched) and status[i][0]['page'] == 0x9d and
                               status[i][0]['transition'] == 4 for i in launched):
                            print(json.dumps(dict(elapsed=round(now-started, 2), checkpoint='sequential-join',
                                                  joined=len(launched), requested=count)), flush=True)
                            break
                        if page == 0x4d and transition == 4:
                            raise RuntimeError(f'Instance {observed} reached an error dialog while joining; inspect its game log')
                    else:
                        raise TimeoutError(f'Instance {instance} did not finish sequential room startup')
            while time.monotonic() - started < args.timeout:
                if args.race and time.monotonic() >= next_finish_check:
                    next_finish_check = time.monotonic()+1
                    for instance, directory in zip(ids, directories):
                        with (directory/'lan-test-game.log').open('rb') as stream:
                            stream.seek(log_positions.get(instance, 0))
                            chunk = stream.read(1024*1024)
                            log_positions[instance] = stream.tell()
                        lines = (log_pending.get(instance, b'')+chunk).split(b'\n')
                        log_pending[instance] = lines.pop()
                        for line in lines:
                            record = finished_progress(line, count)
                            if record is not None:
                                finished[instance] = record
                    if args.finish and len(finished) == count and len({(r['course'], r['mode']) for r in finished.values()}) == 1:
                        finish_since = finish_since or time.monotonic()
                        if time.monotonic()-finish_since >= 2:
                            for instance, process in zip(ids, processes):
                                if process.poll() is not None:
                                    raise RuntimeError(f'Instance {instance} exited with {process.returncode} after finishing')
                            print(json.dumps(dict(result='passed', check=f'all {count} racers finished on every peer',
                                elapsed=round(time.monotonic()-started, 2), test_clock_divisor=test_clock,
                                simulation=finished)), flush=True)
                            return 0
                for instance, process in zip(ids, processes):
                    if process.poll() is not None:
                        raise RuntimeError(f'Instance {instance} exited with {process.returncode}')
                now = time.monotonic()
                if now >= next_summary:
                    snapshot = {str(i): dict(status[i][0], age=round(now-status[i][1], 1))
                                if i in status else dict(missing=True) for i in ids}
                    print(json.dumps(dict(elapsed=round(now-started, 2), checkpoint='room-observations',
                                          peers=snapshot, sdk={str(i): dict(network[i][0], age=round(now-network[i][1], 1)) if i in network else dict(missing=True) for i in ids})), flush=True)
                    next_summary = now + 30
                if args.race and room_confirmed is not None:
                    elapsed = now - room_confirmed
                    for instance, pad in zip(ids, pads):
                        page = status.get(instance, ({}, 0))[0].get('page')
                        buttons, x = 0, 0
                        if elapsed < max(3, args.menu_pulse * 2):
                            # A short left pulse selects Start without menu auto-repeat
                            # wrapping back to the initial Message button.
                            x = -80 if instance == args.host_id and elapsed < args.menu_pulse else 0
                        elif page == 0x42:
                            buttons = 0x100  # accelerate after countdown
                        elif args.finish and race_start_frames:
                            buttons = 0
                        elif not (instance != args.host_id and page == 0x9d):
                            period = max(3, args.menu_pulse * 2)
                            buttons = 0x100 if elapsed % period < max(0.35, args.menu_pulse) else 0
                        controls = buttons, x
                        if last_input.get(instance) != controls:
                            write_pad(pad, buttons, x)
                            last_input[instance] = controls
                try:
                    packet, peer = monitor.recvfrom(4096)
                except socket.timeout:
                    continue
                observation = decode_observation(packet, peer, ids)
                if observation is None:
                    continue
                magic, instance, state, players, section, page, transition = observation
                if magic == 0x4d4b574e:
                    network[instance] = dict(role=state, match=players, players=section,
                                             connected=page, suspended=transition), now
                    continue
                if magic == 0x4d4b5752:
                    # The seven-word race packet contains stage, frames, course,
                    # actual in-race player count and game mode after the ID.
                    record = dict(stage=state, frames=players, course=section, players=page, mode=transition)
                    race[instance] = record, now
                    print(json.dumps(dict(elapsed=round(now-started, 2), instance=instance, race=record)), flush=True)
                    running = all(i in race and now-race[i][1] < args.telemetry_timeout and
                        race[i][0]['stage'] == 2 and race[i][0]['players'] == count for i in ids)
                    if running and all((race[i][0]['course'], race[i][0]['mode']) ==
                            (race[ids[0]][0]['course'], race[ids[0]][0]['mode']) for i in ids):
                        if race_since is None:
                            race_since = now
                            race_start_frames = {i: race[i][0]['frames'] for i in ids}
                        if room_confirmed is not None and now-race_since >= 5 and all(
                                race[i][0]['frames']-race_start_frames[i] >= 120 for i in ids):
                            if args.finish:
                                if not race_reported:
                                    print(json.dumps(dict(checkpoint=f'all {count} peers advancing in the same race',
                                        test_clock_divisor=test_clock, elapsed=round(now-started, 2))), flush=True)
                                    race_reported = True
                            else:
                                print(json.dumps(dict(result='passed', check=f'all {count} peers advancing in the same race', test_clock_divisor=test_clock, elapsed=round(now-started, 2))), flush=True)
                                return 0
                    else:
                        race_since = None
                    continue
                record = dict(state=state, players=players, section=section, page=page, transition=transition)
                now = time.monotonic()
                if status.get(instance, (None,))[0] != record:
                    print(json.dumps(dict(elapsed=round(now-started, 2), instance=instance, **record)), flush=True)
                status[instance] = record, now
                if page == 0x4d and transition == 4:
                    raise RuntimeError(f'Instance {instance} reached an error dialog; inspect its game log')
                joined = all(i in status and now-status[i][1] < args.telemetry_timeout and
                    status[i][0]['state'] == (3 if i == args.host_id else 4) and
                    status[i][0]['players'] == count and status[i][0]['page'] == 0x9d and
                    status[i][0]['transition'] == 4 for i in ids)
                ready_since = (ready_since or now) if joined else None
                if ready_since and now-ready_since >= 5:
                    if not room_reported:
                        print(json.dumps(dict(result='passed', check=f'all {count} peers in one room for five seconds', test_clock_divisor=test_clock, elapsed=round(now-started, 2))), flush=True)
                        room_reported = True
                    if not args.race and not args.mesh:
                        return 0
                    if not sdk_ready(network, ids, args.host_id, count, now, args.telemetry_timeout):
                        continue
                    if room_confirmed is None:
                        print(json.dumps(dict(elapsed=round(now-started, 2), checkpoint='all SDK mesh joins complete')), flush=True)
                        if not args.race:
                            print(json.dumps(dict(result='passed', check=f'all {count} peers have complete SDK connections',
                                test_clock_divisor=test_clock, elapsed=round(now-started, 2))), flush=True)
                            return 0
                        room_confirmed = now
                        # Start the first pulse immediately: waiting for the
                        # next socket iteration can consume the whole pulse.
                        host_pad = pads[ids.index(args.host_id)]
                        write_pad(host_pad, x=-80)
                        last_input[args.host_id] = (0, -80)
            raise TimeoutError('Requested LAN checkpoint timed out; inspect telemetry and lan-test-game.log files')
        finally:
            for pad in pads:
                write_pad(pad)
            for process in processes:
                if process.poll() is None:
                    process.terminate()
            for process in processes:
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            for log in logs:
                log.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--client', type=Path, action='append', required=True, help='Repeat for additional portable client directories')
    parser.add_argument('--timeout', type=float, default=180)
    parser.add_argument('--sequential-join', action='store_true', help='Wait for every launched peer to report the expected active room before launching the next client')
    parser.add_argument('--host-first', action='store_true', help='Wait for the host room before starting all clients; client admission remains concurrent')
    parser.add_argument('--telemetry-timeout', type=float, default=3, help='Maximum age of an observation on slow multi-instance test hosts; gameplay checks still require every peer to advance')
    parser.add_argument('--startup-gap', type=float, default=0, help='Seconds between process launches')
    parser.add_argument('--menu-pulse', type=float, default=0.2, help='Seconds to hold menu inputs; increase on slow multi-instance hosts')
    parser.add_argument('--capacity', type=int, choices=range(1, 33), default=12, help='Capacity of the matched guest/module build')
    parser.add_argument('--host-id', type=int, choices=range(1, 33), default=1)
    parser.add_argument('--client-id', type=int, choices=range(1, 33), action='append', help='Optional IDs in client-directory order')
    parser.add_argument('--mesh', action='store_true', help='Require complete SDK connections without starting a race')
    parser.add_argument('--race', action='store_true', help='Also navigate menus and require every race simulation to advance')
    parser.add_argument('--finish', action='store_true', help='Drive local racers with the CPU and require every peer to observe all race finishes')
    parser.add_argument('--visible', action='store_true', help='Run the same checks with a visible game window on each peer')
    options = parser.parse_args()
    options.race = options.race or options.finish
    try:
        raise SystemExit(run(options))
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        parser.exit(1, f'{error}\n')
