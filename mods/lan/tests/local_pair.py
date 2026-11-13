#!/usr/bin/env python3
"""Run portable LAN instances with independent saves and verify room/race progress.

Run inside one host/network namespace with a working SDL display backend.
A license in slot zero must already exist in each product's private NAND.
"""
import argparse
import hashlib
import json
import os
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
    if magic not in (0x4d4b574c, 0x4d4b5752) or instance not in ids:
        return None
    if peer[0] != f'127.0.0.{instance}':
        raise RuntimeError(f'Instance {instance} used unexpected source address {peer}')
    return words


def run(args):
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
    status, race, last_input = {}, {}, {}
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
                processes.append(subprocess.Popen([str(executable), '--headless', '--lan-local', f'--lan-id={instance}', f'--lan-peers={args.capacity}', role], cwd=directory,
                    env=environment, stdout=log, stderr=subprocess.STDOUT))
                if args.sequential_join:
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
                        if observed == instance and page == 0x4d and transition == 4:
                            raise RuntimeError(f'Instance {instance} reached an error dialog while joining; inspect its game log')
                    else:
                        raise TimeoutError(f'Instance {instance} did not finish sequential room startup')
            while time.monotonic() - started < args.timeout:
                for instance, process in zip(ids, processes):
                    if process.poll() is not None:
                        raise RuntimeError(f'Instance {instance} exited with {process.returncode}')
                now = time.monotonic()
                if now >= next_summary:
                    snapshot = {str(i): dict(status[i][0], age=round(now-status[i][1], 1))
                                if i in status else dict(missing=True) for i in ids}
                    print(json.dumps(dict(elapsed=round(now-started, 2), checkpoint='room-observations',
                                          peers=snapshot)), flush=True)
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
                if magic == 0x4d4b5752:
                    # The seven-word race packet contains stage, frames, course,
                    # actual in-race player count and game mode after the ID.
                    record = dict(stage=state, frames=players, course=section, players=page, mode=transition)
                    race[instance] = record, now
                    print(json.dumps(dict(elapsed=round(now-started, 2), instance=instance, race=record)), flush=True)
                    running = all(i in race and now-race[i][1] < args.telemetry_timeout and
                        race[i][0]['stage'] == 2 and race[i][0]['players'] == count for i in ids)
                    if running and all(race[i][0]['course'] == race[ids[0]][0]['course'] for i in ids):
                        if race_since is None:
                            race_since = now
                            race_start_frames = {i: race[i][0]['frames'] for i in ids}
                        if room_confirmed is not None and now-race_since >= 5 and all(
                                race[i][0]['frames']-race_start_frames[i] >= 120 for i in ids):
                            print(json.dumps(dict(result='passed', check=f'all {count} peers advancing in the same race')), flush=True)
                            return 0
                    else:
                        race_since = None
                    continue
                record = dict(state=state, players=players, section=section, page=page, transition=transition)
                now = time.monotonic()
                if status.get(instance, (None,))[0] != record:
                    print(json.dumps(dict(elapsed=round(now-started, 2), instance=instance, **record)), flush=True)
                status[instance] = record, now
                joined = all(i in status and now-status[i][1] < args.telemetry_timeout and
                    status[i][0]['state'] == (3 if i == args.host_id else 4) and
                    status[i][0]['players'] == count and status[i][0]['page'] == 0x9d and
                    status[i][0]['transition'] == 4 for i in ids)
                ready_since = (ready_since or now) if joined else None
                if ready_since and now-ready_since >= 5:
                    if room_confirmed is None:
                        print(json.dumps(dict(result='passed', check=f'all {count} peers in one room for five seconds')), flush=True)
                    if not args.race:
                        return 0
                    if room_confirmed is None:
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
    parser.add_argument('--telemetry-timeout', type=float, default=3, help='Maximum age of an observation on slow multi-instance test hosts; gameplay checks still require every peer to advance')
    parser.add_argument('--startup-gap', type=float, default=0, help='Seconds between process launches')
    parser.add_argument('--menu-pulse', type=float, default=0.2, help='Seconds to hold menu inputs; increase on slow multi-instance hosts')
    parser.add_argument('--capacity', type=int, choices=range(1, 33), default=12, help='Capacity of the matched guest/module build')
    parser.add_argument('--host-id', type=int, choices=range(1, 33), default=1)
    parser.add_argument('--client-id', type=int, choices=range(1, 33), action='append', help='Optional IDs in client-directory order')
    parser.add_argument('--race', action='store_true', help='Also navigate menus and require every race simulation to advance')
    options = parser.parse_args()
    try:
        raise SystemExit(run(options))
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        parser.exit(1, f'{error}\n')
