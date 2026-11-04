#!/usr/bin/env python3
"""Run two portable LAN products with independent saves and assert peer discovery.

Run inside one host/network namespace with a working SDL display backend.
A license in slot zero must already exist in each product's private NAND.
"""
import argparse
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


def run(args):
    directories = [args.host.resolve(), args.client.resolve()]
    if directories[0] == directories[1]:
        raise ValueError('Host and client require different portable directories')
    processes, logs, pads = [], [], []
    status = {}
    started = time.monotonic()
    ready_since = None
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as monitor:
        # Bind before launching either process. Failure means another test owns it.
        monitor.bind(('127.0.0.254', 27900))
        monitor.settimeout(0.2)
        try:
            for directory in directories:
                executable = directory / ('WiiCompiledLAN.exe' if os.name == 'nt' else 'WiiCompiledLAN')
                if not executable.is_file() or not (directory / 'portable.txt').is_file():
                    raise ValueError(f'Expected a portable LAN product in {directory}')
                pad = directory / 'lan-test-pad.bin'
                write_pad(pad)
                pads.append(pad)
                log = (directory / 'lan-test-game.log').open('w')
                logs.append(log)
                environment = dict(os.environ, WIICOMPILED_TEST_PAD_FILE=str(pad))
                processes.append(subprocess.Popen([str(executable), '--headless'], cwd=directory,
                    env=environment, stdout=log, stderr=subprocess.STDOUT))
            while time.monotonic() - started < args.timeout:
                for instance, process in enumerate(processes, 1):
                    if process.poll() is not None:
                        raise RuntimeError(f'Instance {instance} exited with {process.returncode}')
                try:
                    packet, peer = monitor.recvfrom(4096)
                except socket.timeout:
                    continue
                if len(packet) != 28:
                    continue
                magic, instance, state, players, section, page, transition = struct.unpack('>7I', packet)
                if magic != 0x4d4b574c or instance not in (1, 2):
                    continue
                if peer[0] != f'127.0.0.{instance}':
                    raise RuntimeError(f'Instance {instance} used unexpected source address {peer}')
                record = dict(state=state, players=players, section=section, page=page, transition=transition)
                now = time.monotonic()
                if status.get(instance, (None,))[0] != record:
                    print(json.dumps(dict(elapsed=round(now-started, 2), instance=instance, **record)), flush=True)
                status[instance] = record, now
                joined = all(i in status and now-status[i][1] < 3 and
                    status[i][0]['state'] == (3 if i == 1 else 4) and
                    status[i][0]['players'] == 2 and status[i][0]['page'] == 0x9d and
                    status[i][0]['transition'] == 4 for i in (1, 2))
                ready_since = (ready_since or now) if joined else None
                if ready_since and now-ready_since >= 5:
                    print(json.dumps(dict(result='passed', check='two peers in one room for five seconds')), flush=True)
                    return 0
            raise TimeoutError('Both instances did not sustain a two-player room; inspect lan-test-game.log files')
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
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--timeout', type=float, default=90)
    options = parser.parse_args()
    try:
        raise SystemExit(run(options))
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        parser.exit(1, f'{error}\n')
