#!/usr/bin/env python3
"""Observe a real USA CPU race/battle without writing guest RAM or scaling time.

Sequential snapshots are not atomic. Sampled distance is a lower bound, and
sampled respawns can miss short events. Native finish flags do not establish
that every CPU completed a course: retain each slot's lap/movement evidence.
"""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

from forced_falls import Ram, racers


def battle_timer_evidence(ram, info, players, mode, course):
    """Read the verified USA KMG row independently of the constructor lookup."""
    timers, holder = ram.u32(info + 0x14), ram.u32(info + 0x3C)
    if not timers or not holder:
        raise RuntimeError("Battle timer or KMG holder is unavailable")
    table = ram.u32(holder + 4)
    if not table or ram.u32(table) != 0x524B4D47:
        raise RuntimeError("Battle KMG table is unavailable or invalid")
    # Original KMGHolder::ConvertArenaIdToIdx (USA 805337FC).
    arenas = [ram.u32(0x8088BDC8 + i * 4) for i in range(10)]
    if course not in arenas:
        raise RuntimeError(f"Battle course {course} is absent from the original arena map")
    arena = arenas.index(course)
    authored_count = min(12, max(2, players))
    seconds = ram.u16(table + (0x10 if mode == "balloon" else 0x1DC) +
                      arena * 22 + (authored_count - 2) * 2)

    def milliseconds(offset):
        return (ram.u16(timers + offset) * 60000 +
                ram.u8(timers + offset + 2) * 1000 + ram.u16(timers + offset + 4))

    return dict(limit_ms=ram.u32(timers + 0x44), initial_ms=milliseconds(0x20),
                remaining_ms=milliseconds(0x14), authored_limit_ms=seconds * 1000,
                authored_player_count=authored_count, arena_index=arena)


def validate_battle_timer(evidence, completed=False):
    expected = evidence["authored_limit_ms"]
    if expected <= 0 or evidence["limit_ms"] != expected or evidence["initial_ms"] != expected:
        raise RuntimeError(f"Battle duration does not match its authored KMG row: {evidence}")
    if completed and evidence["remaining_ms"] > 1000:
        raise RuntimeError(f"Battle ended before its countdown expired: {evidence}")


def observed_roster(rows, diverse=False):
    characters = sorted({row["character"] for row in rows})
    vehicles = sorted({row["vehicle"] for row in rows})
    if diverse and (len(characters) != min(len(rows), 24) or
                    len(vehicles) != min(len(rows), 36) or
                    any(character >= 24 for character in characters) or
                    any(vehicle >= 36 for vehicle in vehicles)):
        raise RuntimeError("Live roster does not contain the requested vanilla character/vehicle diversity")
    return dict(characters=characters, vehicles=vehicles,
                players=[dict(slot=row["slot"], character=row["character"], vehicle=row["vehicle"])
                         for row in rows])


def run(args):
    if sys.platform != "linux":
        raise ValueError("The shared RAM observer currently requires Linux")
    if not 1 <= args.players <= args.capacity <= 255:
        raise ValueError("Expected 1 <= players <= capacity <= 255")
    if args.timeout <= 0 or args.sample_frames <= 0:
        raise ValueError("Timeout and sampling interval must be positive")
    product = args.product.resolve()
    binary = product / args.binary
    if (product / "DATA/sys/boot.bin").read_bytes()[:6] != b"RMCE01":
        raise ValueError("This observer requires the verified USA guest ABI")
    args.log = args.log.resolve()
    args.report = args.report.resolve()
    args.audit_report = args.audit_report.resolve()
    for path in (args.log, args.report, args.audit_report):
        path.parent.mkdir(parents=True, exist_ok=True)
    if len({args.log, args.report, args.audit_report}) != 3:
        raise ValueError("Log, report and auditor output paths must differ")
    command = [str(binary), "--headless", "--race-cpu",
               f"--race-players={args.players}", f"--race-mode={args.mode}",
               f"--layout-audit={args.audit}",
               f"--layout-audit-report={args.audit_report}"]
    if args.course is not None:
        command.append(f"--race={args.course}")
    else:
        command.append("--race")
    command.append(f"--race-roster={args.roster}")
    battle = args.mode in {"balloon", "coin"}
    expected_mode = 3 if battle else 0 if args.mode == "grand-prix" else 1
    if not battle:
        command.append(f"--race-laps={args.laps}")
    report = dict(result="incomplete", source=args.source,
                  binary_sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
                  players=args.players, capacity=args.capacity, mode=args.mode, roster=args.roster,
                  course=args.course, laps=None if battle else args.laps,
                  clock_divisor=1, command=command, snapshots=[], slots=[],
                  limitations=__doc__)
    user_data = product / "UserData"
    user_data.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="capacity-observer-", dir=user_data) as temporary:
        folder = Path(temporary)
        preload = folder / "guest-ram-backing.so"
        subprocess.run([args.cc, "-shared", "-fPIC", "-O2",
                        str(Path(__file__).with_name("guest_ram_backing.c")),
                        "-ldl", "-o", str(preload)], check=True)
        env = dict(os.environ, WIICOMPILED_TEST_RAM_DIR=str(folder),
                   LD_PRELOAD=str(preload) + (":" + os.environ["LD_PRELOAD"]
                       if os.environ.get("LD_PRELOAD") else ""))
        # Do not inherit test clock or controller injection from another probe.
        for key in ("MKW_TEST_CLOCK_DIVISOR", "WIICOMPILED_TEST_PAD_FILE", "MKW_TEST_LAN_CPU"):
            env.pop(key, None)
        with args.log.open("w") as log:
            process = subprocess.Popen(command, cwd=product, env=env,
                                       stdout=log, stderr=subprocess.STDOUT)
            started = time.monotonic()
            ram, base, progress, last_sample = None, None, None, None
            previous = {}
            try:
                with args.log.open(errors="replace") as reader:
                    pending = ""
                    while time.monotonic() - started < args.timeout:
                        pending += reader.read()
                        lines = []
                        if "\n" in pending:
                            complete, pending = pending.rsplit("\n", 1)
                            lines = complete.splitlines()
                        for line in lines:
                            match = re.search(r"guest address space reserved at (0x[0-9a-f]+)", line)
                            if match:
                                base = int(match[1], 16)
                            if any(marker in line for marker in (
                                "FATAL", "WARNING unmapped guest touch", "scheduler could not switch away")):
                                raise RuntimeError(f"Guest fault: {line}")
                            if "[race-progress]" in line:
                                progress = {key: int(value) for key, value in
                                            re.findall(r"([a-z_]+)=(\d+)", line)}
                                if any(progress.get(key) != value for key, value in (
                                    ("players", args.players), ("observed", args.players),
                                    ("capacity", args.capacity), ("mode", expected_mode))):
                                    raise RuntimeError(f"Unexpected race capacity: {line}")
                        if progress and progress["stage"] in (2, 3, 4) and base:
                            if ram is None:
                                ram = Ram(process, folder, base, read_only=True)
                            info, rows = racers(ram, args.players)
                            stage, frame = ram.u32(info + 0x28), ram.u32(info + 0x20)
                            sample = stage, frame // args.sample_frames
                            if sample != last_sample:
                                last_sample = sample
                                snapshot = dict(stage=stage, frame=frame, racers=rows)
                                if battle:
                                    timer = battle_timer_evidence(ram, info, args.players, args.mode, progress["course"])
                                    report.setdefault("battle_timer_at_first_sample", timer)
                                    snapshot["battle_timer"] = timer
                                    validate_battle_timer(timer)
                                report["snapshots"].append(snapshot)
                                if not report["slots"]:
                                    report["observed_roster"] = observed_roster(rows, diverse=args.roster == "diverse")
                                    report["slots"] = [dict(slot=i, sampled_distance=0.0,
                                        respawn_samples=0, min_lap=row["lap"], max_lap=row["lap"],
                                        max_stationary_frames=0, samples=0) for i, row in enumerate(rows)]
                                for row, record in zip(rows, report["slots"]):
                                    record["samples"] += 1
                                    record["respawn_samples"] += int(row["respawning"])
                                    record["min_lap"] = min(record["min_lap"], row["lap"])
                                    record["max_lap"] = max(record["max_lap"], row["lap"])
                                    old = previous.get(row["slot"])
                                    stationary_since = frame
                                    if old:
                                        distance = math.dist(old["position"], row["position"])
                                        record["sampled_distance"] += distance
                                        if distance < 1 and stage == 2:
                                            stationary_since = old["stationary_since"]
                                            record["max_stationary_frames"] = max(
                                                record["max_stationary_frames"], frame - stationary_since)
                                    previous[row["slot"]] = dict(position=row["position"],
                                                                 stationary_since=stationary_since)
                                print(json.dumps(dict(stage=stage, frame=frame, players=len(rows))), flush=True)
                            if progress["stage"] == 4 and progress["finished"] == args.players:
                                if battle:
                                    timer = battle_timer_evidence(ram, info, args.players, args.mode, progress["course"])
                                    report["battle_timer_at_completion"] = timer
                                    validate_battle_timer(timer, completed=True)
                                report.update(result="native-finish-flags", completion=progress)
                                report["slots_without_sampled_movement"] = [r["slot"] for r in report["slots"]
                                                                            if r["sampled_distance"] < 200]
                                report["slots_without_sampled_lap_advance"] = [] if battle else [
                                    r["slot"] for r in report["slots"] if r["max_lap"] <= r["min_lap"]]
                                report["slots_without_sampled_finish_lap"] = [] if battle else [
                                    r["slot"] for r in report["slots"] if r["max_lap"] <= args.laps]
                                return report
                        if process.poll() is not None:
                            raise RuntimeError(f"Game exited: {process.returncode}")
                        time.sleep(.1)
                raise TimeoutError("Race did not reach all native finish flags")
            except Exception as error:
                report["error"] = str(error)
                raise
            finally:
                report["elapsed_seconds"] = time.monotonic() - started
                args.report.write_text(json.dumps(report, indent=2) + "\n")
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()
                if ram:
                    ram.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--product", type=Path, required=True)
    parser.add_argument("--binary", default="WiiCompiled")
    parser.add_argument("--source", required=True, help="Revision used to build the supplied binary")
    parser.add_argument("--cc", default=os.environ.get("CC", "cc"))
    parser.add_argument("--players", type=int, required=True)
    parser.add_argument("--capacity", type=int, required=True)
    parser.add_argument("--course", type=lambda s: int(s, 0))
    parser.add_argument("--mode", choices=("vs", "team-vs", "grand-prix", "balloon", "coin"), default="vs")
    parser.add_argument("--roster", choices=("standard", "diverse"), default="standard")
    parser.add_argument("--laps", type=int, choices=range(1, 10), default=1)
    parser.add_argument("--sample-frames", type=int, default=120)
    parser.add_argument("--timeout", type=float, default=900)
    parser.add_argument("--audit", choices=("off", "log", "strict"), default="log")
    for name in ("log", "report", "audit-report"):
        parser.add_argument("--" + name, type=Path, required=True)
    try:
        run(parser.parse_args())
    except (OSError, ValueError, RuntimeError, TimeoutError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) + "\n")
