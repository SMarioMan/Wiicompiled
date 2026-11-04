#!/usr/bin/env python3
"""Create license zero through normal menus in a fresh portable USA LAN product."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time
import zlib

from local_pair import write_pad


def read_license(path):
    try:
        with path.open('rb') as save:
            data = save.read(0x28000)
    except FileNotFoundError:
        return False, False
    exists = data[8:12] == b'RKPD'
    valid = len(data) == 0x28000 and zlib.crc32(data[:-4]) == int.from_bytes(data[-4:], 'big')
    return exists, valid


def run(args):
    directory = args.product.resolve()
    executable = directory / ('WiiCompiledLAN.exe' if os.name == 'nt' else 'WiiCompiledLAN')
    if not executable.is_file() or not (directory / 'portable.txt').is_file():
        raise ValueError(f'Expected a portable LAN product in {directory}')
    save = directory / 'UserData/NAND/title/00010004/524d4345/data/rksys.dat'
    if read_license(save)[0]:
        raise ValueError('License zero already exists; this check requires a fresh private product')
    pad = directory / 'lan-profile-pad.bin'
    write_pad(pad)
    with (directory / 'lan-profile-game.log').open('w') as log:
        process = subprocess.Popen([str(executable), '--headless'], cwd=directory,
            env=dict(os.environ, WIICOMPILED_TEST_PAD_FILE=str(pad)), stdout=log, stderr=subprocess.STDOUT)
        started = time.monotonic()
        previous = None
        try:
            while time.monotonic()-started < args.timeout:
                if process.poll() is not None:
                    raise RuntimeError(f'Game exited with {process.returncode} before saving a license')
                elapsed = time.monotonic()-started
                buttons = 0x100 if elapsed > 8 and elapsed % 3 < 0.35 else 0
                if buttons != previous:
                    write_pad(pad, buttons)
                    previous = buttons
                if all(read_license(save)):
                    print(json.dumps(dict(result='passed', check='license zero created by menu input with valid save CRC')), flush=True)
                    return 0
                time.sleep(0.1)
            raise TimeoutError('No saved license; inspect lan-profile-game.log')
        finally:
            write_pad(pad)
            process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--product', type=Path, required=True)
    parser.add_argument('--timeout', type=float, default=180)
    try:
        raise SystemExit(run(parser.parse_args()))
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        parser.exit(1, f'{error}\n')
