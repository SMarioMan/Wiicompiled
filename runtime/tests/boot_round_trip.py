#!/usr/bin/env python3
"""Drive a portable VS race through finish, menu confirmation and a second race."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time

PROGRESS = re.compile(r"\[race-progress\] (.*)")
FIELDS = re.compile(r"([a-z_]+)=(\d+)")
REQUIRED = {"stage", "frames", "players", "capacity", "observed", "min_player_frames",
            "course", "mode", "finished", "min_lap", "max_lap"}


def write_pad(path, buttons=0):
    temporary = path.with_suffix(".tmp")
    temporary.write_bytes(buttons.to_bytes(2, "big"))
    temporary.replace(path)


def run(args):
    product = args.product.resolve()
    executable = product / ("WiiCompiled.exe" if os.name == "nt" else "WiiCompiled")
    if not executable.is_file() or not (product / "portable.txt").is_file():
        raise ValueError("Expected a portable base product with WiiCompiled and portable.txt")
    args.log.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="race-round-trip-", dir=product / "UserData") as directory:
        pad = Path(directory) / "pad.bin"
        write_pad(pad)
        with args.log.open("w") as log:
            process = subprocess.Popen([str(executable), "--headless", "--race-cpu-menu-input",
                f"--race-players={args.players}", f"--race-laps={args.laps}"], cwd=product,
                env=dict(os.environ, WIICOMPILED_TEST_PAD_FILE=str(pad)),
                stdout=log, stderr=subprocess.STDOUT)
            started = time.monotonic()
            finished = False
            restarted = False
            previous_buttons = 0
            try:
                with args.log.open(encoding="utf-8", errors="replace") as reader:
                    pending = ""
                    while time.monotonic() - started < args.timeout:
                        pending += reader.read()
                        if "\n" in pending:
                            complete, pending = pending.rsplit("\n", 1)
                            lines = complete.split("\n")
                        else:
                            lines = []
                        for line in lines:
                            if any(marker in line for marker in (
                                "FATAL", "scheduler could not switch away", "WARNING unmapped guest touch")):
                                raise RuntimeError(f"Guest fault: {line.strip()}")
                            match = PROGRESS.search(line)
                            if not match:
                                continue
                            progress = {key: int(value) for key, value in FIELDS.findall(match[1])}
                            if not REQUIRED.issubset(progress):
                                continue
                            if progress["players"] != args.players or progress["observed"] != args.players:
                                raise RuntimeError(f"Incomplete field: {line.strip()}")
                            if progress["stage"] == 4 and progress["finished"] == args.players:
                                if progress["min_lap"] < args.laps:
                                    raise RuntimeError("Race ended before every racer reached the final lap")
                                finished = True
                            elif finished and progress["stage"] == 0:
                                restarted = True
                            elif restarted and progress["stage"] == 2 and progress["frames"] >= 300:
                                print(json.dumps(dict(result="passed", players=args.players, laps=args.laps,
                                    check="finish, menus, second race advancing", progress=progress)), flush=True)
                                return 0
                        if process.poll() is not None:
                            raise RuntimeError(f"Game exited with {process.returncode}; inspect {args.log}")
                        buttons = 0x100 if finished and not restarted and (time.monotonic()-started) % 1 < .5 else 0
                        if buttons != previous_buttons:
                            write_pad(pad, buttons)
                            previous_buttons = buttons
                        time.sleep(.1)
                raise TimeoutError(f"Race round trip did not complete; inspect {args.log}")
            finally:
                write_pad(pad)
                process.terminate()
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--product", type=Path, required=True)
    parser.add_argument("--players", type=int, required=True, choices=range(1, 256), metavar="N")
    parser.add_argument("--laps", type=int, default=1, choices=range(1, 10))
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=300)
    try:
        raise SystemExit(run(parser.parse_args()))
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        parser.exit(1, f"{error}\n")
