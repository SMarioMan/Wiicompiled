"""USA-only developer probe: force real falls without setting rescue flags."""

import argparse
import hashlib
import json
import math
import os
import re
import signal
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path


class Ram:
    def __init__(self, process, folder, guest_base):
        self.process, self.folder, self.guest_base = (
            process,
            folder.resolve(),
            guest_base,
        )
        self.regions, self.fds = [], {}
        for guest, size in (
            (0x80000000, 24 * 1024 * 1024),
            (0x90000000, 128 * 1024 * 1024),
        ):
            address = guest_base + guest
            for line in Path(f"/proc/{process.pid}/maps").read_text().splitlines():
                fields = line.split(maxsplit=5)
                if len(fields) != 6:
                    continue
                begin, end = (int(x, 16) for x in fields[0].split("-"))
                if begin <= address < end:
                    path = Path(fields[5]).resolve()
                    if path.parent != self.folder:
                        raise RuntimeError(
                            f"Guest RAM does not use this test directory: {path}"
                        )
                    if path not in self.fds:
                        self.fds[path] = os.open(path, os.O_RDWR)
                    offset = int(fields[2], 16) + address - begin
                    self.regions.append((guest, guest + size, self.fds[path], offset))
                    break
            else:
                raise RuntimeError(f"No current RAM alias for {guest:#x}")

    def address(self, address, size):
        for begin, end, fd, offset in self.regions:
            if begin <= address and address + size <= end:
                return fd, offset + address - begin
        raise RuntimeError(f"Unmapped guest access {address:#x}+{size}")

    def read(self, address, size):
        fd, offset = self.address(address, size)
        value = os.pread(fd, size, offset)
        if len(value) != size:
            raise RuntimeError("Short guest RAM read")
        return value

    def u32(self, address):
        return struct.unpack(">I", self.read(address, 4))[0]

    def u16(self, address):
        return struct.unpack(">H", self.read(address, 2))[0]

    def u8(self, address):
        return self.read(address, 1)[0]

    def vec(self, address):
        return struct.unpack(">3f", self.read(address, 12))

    def put_float(self, address, value):
        fd, offset = self.address(address, 4)
        if os.pwrite(fd, struct.pack(">f", value), offset) != 4:
            raise RuntimeError("Short guest RAM write")

    def close(self):
        for fd in self.fds.values():
            os.close(fd)


def freeze(process):
    os.kill(process.pid, signal.SIGSTOP)
    until = time.monotonic() + 5
    while time.monotonic() < until:
        pid, status = os.waitpid(process.pid, os.WUNTRACED | os.WNOHANG)
        if pid:
            if os.WIFSTOPPED(status):
                return
            process.returncode = os.waitstatus_to_exitcode(status)
            raise RuntimeError("Game exited before the memory checkpoint")
        time.sleep(0.01)
    raise RuntimeError("Game did not stop for the memory checkpoint")


def racers(ram, count):
    # Verified USA Kart::Link accessors: GetPlayerIdx (8058A238),
    # GetKartPosition (805899E8), GetPreviousPosition (80589A00),
    # IsRespawning (8058AAF0). These are data fields, never code patches.
    manager = ram.u32(0x809BD110)
    players = ram.u32(manager + 0x20)
    info = ram.u32(0x809B8F70)
    race_players = ram.u32(info + 0x0C)
    result = []
    for i in range(count):
        player = ram.u32(players + i * 4)
        pointers = ram.u32(player)
        values = ram.u32(pointers)
        if ram.u8(values + 0x10) != i:
            raise RuntimeError(f"Kart slot {i} has another player ID")
        sub = ram.u32(pointers + 8)
        holder = ram.u32(sub + 0x90)
        physics = ram.u32(holder + 4)
        status = ram.u32(pointers + 4)
        position = physics + 0x68
        previous = holder + 0x18
        race_player = ram.u32(race_players + i * 4)
        respawning = bool(ram.u32(status + 8) & 2 or ram.u32(status + 12) & 0x86000)
        point = ram.vec(position)
        if not all(math.isfinite(x) for x in point):
            raise RuntimeError(f"Non-finite position at slot {i}")
        result.append(
            dict(
                slot=i,
                position_address=position,
                previous_address=previous,
                position=point,
                respawning=respawning,
                player_frames=ram.u32(race_player + 0x2C),
                lap=ram.u16(race_player + 0x24),
            )
        )
    if len({r["position_address"] for r in result}) != count:
        raise RuntimeError("Different racers share a physics position")
    return info, result


def run(args):
    if not 1 <= args.players <= 255:
        raise ValueError("Player count must fit the guest count byte")
    if not math.isfinite(args.drop) or args.drop <= 0:
        raise ValueError("Fall distance must be finite and positive")
    if args.fall_frame < 0 or args.timeout <= 0:
        raise ValueError("Frame and timeout arguments are invalid")
    if sys.platform != "linux":
        raise RuntimeError("The shared RAM probe currently requires Linux")
    product = args.product.resolve()
    binary = product / args.binary
    if (product / "DATA/sys/boot.bin").read_bytes()[:6] != b"RMCE01":
        raise ValueError("This pointer probe requires the verified USA guest ABI")
    report = dict(
        result="incomplete",
        binary_sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
        players=args.players,
        course=args.course,
        mode=args.mode,
        slots={},
    )
    args.log.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="forced-falls-", dir=product / "UserData"
    ) as temporary:
        folder = Path(temporary)
        pad = folder / "pad.bin"
        pad.write_bytes(bytes(4))
        preload = folder / "guest-ram-backing.so"
        subprocess.run(
            [
                args.cc,
                "-shared",
                "-fPIC",
                "-O2",
                str(Path(__file__).with_name("guest_ram_backing.c")),
                "-ldl",
                "-o",
                str(preload),
            ],
            check=True,
        )
        env = dict(
            os.environ,
            WIICOMPILED_TEST_RAM_DIR=str(folder),
            WIICOMPILED_TEST_PAD_FILE=str(pad),
            LD_PRELOAD=str(preload)
            + (":" + os.environ["LD_PRELOAD"] if os.environ.get("LD_PRELOAD") else ""),
        )
        with args.log.open("w") as log:
            process = subprocess.Popen(
                [
                    str(binary),
                    "--headless",
                    f"--race={args.course}",
                    "--race-cpu-menu-input",
                    f"--race-players={args.players}",
                    f"--race-mode={args.mode}",
                    "--race-laps=3",
                ],
                cwd=product,
                env=env,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            ram = None
            started = time.monotonic()
            racing_since = None
            injected = False
            try:
                while time.monotonic() - started < args.timeout:
                    if process.poll() is not None:
                        raise RuntimeError(f"Game exited: {process.returncode}")
                    if ram is None:
                        text = args.log.read_text(errors="replace")
                        match = re.search(
                            r"guest address space reserved at (0x[0-9a-f]+)", text
                        )
                        if not match or "[race-progress] stage=2 " not in text:
                            time.sleep(0.1)
                            continue
                        ram = Ram(process, folder, int(match[1], 16))
                    info, rows = racers(ram, args.players)
                    stage = ram.u32(info + 0x28)
                    frames = ram.u32(info + 0x20)
                    if stage == 2 and racing_since is None:
                        racing_since = frames
                    if stage == 4:
                        raise RuntimeError(
                            "Race ended before every forced fall recovered"
                        )
                    if (
                        not injected
                        and racing_since is not None
                        and frames - racing_since >= args.fall_frame
                        and not any(row["respawning"] for row in rows)
                    ):
                        freeze(process)
                        try:
                            info, rows = racers(ram, args.players)
                            frames = ram.u32(info + 0x20)
                            height = min(row["position"][1] for row in rows) - args.drop
                            for row in rows:
                                ram.put_float(row["position_address"] + 4, height)
                                ram.put_float(row["previous_address"] + 4, height)
                                report["slots"][row["slot"]] = dict(
                                    before=row["position"],
                                    before_player_frames=row["player_frames"],
                                    before_lap=row["lap"],
                                    lap_progress=None,
                                    seen_respawn=None,
                                    recovered=None,
                                    moved=None,
                                )
                            report.update(injected_frame=frames, fall_height=height)
                            injected = True
                            print(
                                json.dumps(
                                    dict(
                                        checkpoint="forced-fall",
                                        frame=frames,
                                        players=args.players,
                                        height=height,
                                    )
                                ),
                                flush=True,
                            )
                        finally:
                            os.kill(process.pid, signal.SIGCONT)
                    elif injected:
                        for row in rows:
                            record = report["slots"][row["slot"]]
                            if (
                                not row["respawning"]
                                and row["lap"] > record["before_lap"]
                            ):
                                record["lap_progress"] = frames
                            if row["respawning"] and record["seen_respawn"] is None:
                                record["seen_respawn"] = frames
                                print(
                                    json.dumps(
                                        dict(
                                            checkpoint="respawning",
                                            slot=row["slot"],
                                            frame=frames,
                                        )
                                    ),
                                    flush=True,
                                )
                            if (
                                record["seen_respawn"] is not None
                                and not row["respawning"]
                                and row["position"][1] > record["before"][1] - 5000
                            ):
                                if record["recovered"] is None:
                                    record.update(
                                        recovered=frames,
                                        recovery_position=row["position"],
                                    )
                                distance = math.hypot(
                                    row["position"][0] - record["recovery_position"][0],
                                    row["position"][2] - record["recovery_position"][2],
                                )
                                if (
                                    frames - record["recovered"] >= 120
                                    and distance > 200
                                    and record["moved"] is None
                                ):
                                    record.update(
                                        moved=frames,
                                        distance=distance,
                                        after=row["position"],
                                        after_player_frames=row["player_frames"],
                                    )
                        if all(
                            record["moved"] is not None
                            and record["lap_progress"] is not None
                            for record in report["slots"].values()
                        ):
                            report.update(result="passed", last_frame=frames)
                            print(
                                json.dumps(
                                    dict(
                                        result="passed",
                                        check=f"all {args.players} slots rescued, moving and advancing laps",
                                    )
                                ),
                                flush=True,
                            )
                            return report
                    time.sleep(0.05)
                args.report.write_text(json.dumps(report, indent=2) + "\n")
                raise TimeoutError(
                    "Forced-fall checkpoint timed out: " + json.dumps(report)
                )
            except Exception as error:
                report["error"] = str(error)
                raise
            finally:
                args.report.parent.mkdir(parents=True, exist_ok=True)
                args.report.write_text(json.dumps(report, indent=2) + "\n")
                if process.poll() is None:
                    os.kill(process.pid, signal.SIGCONT)
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
    parser.add_argument("--binary", default="WiiCompiledLAN")
    parser.add_argument(
        "--cc",
        default=os.environ.get("CC", "cc"),
        help="Host C compiler for the test-only RAM backing helper",
    )
    parser.add_argument("--players", type=int, default=24)
    parser.add_argument("--course", type=lambda s: int(s, 0), default=8)
    parser.add_argument("--mode", choices=("vs", "team-vs", "grand-prix"), default="vs")
    parser.add_argument("--fall-frame", type=int, default=360)
    parser.add_argument("--drop", type=float, default=200000)
    parser.add_argument("--timeout", type=float, default=300)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    a = parser.parse_args()
    try:
        result = run(a)
        a.report.parent.mkdir(parents=True, exist_ok=True)
        a.report.write_text(json.dumps(result, indent=2) + "\n")
    except (
        OSError,
        ValueError,
        RuntimeError,
        TimeoutError,
        subprocess.CalledProcessError,
    ) as e:
        parser.exit(1, str(e) + "\n")
