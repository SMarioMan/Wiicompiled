# 128-player LAN status

The Linux 128-capacity build has completed offline VS, Team VS, Grand Prix,
Balloon Battle, Coin Runners, and Time Trial validation, as well as **live
128-console LAN multiplayer** (`IDs 1..128`, `AIDs 0..127`). Time Trial retains
its single-racer rules. See [offline results](128-racer-comparison.md) and
[LAN results](lan-expanded-validation.md).

## 128-player LAN architecture & fixes

- **Expanded SDK & RKNet storage (`3b1ac4b`..`a4a4e26`)**: Widened `DWCMatch`
  nodes (128 slots), `DWCTransport` records, connection pointer/metadata tables,
  128-bit `suspend_votes` and `RKNetSub` / `RKNetSELECT` / `RKNetRH1` /
  `RKNetRACE` bitfields, and raised `DWCTransport::send_split_max` so 128-peer
  SYN packets are never truncated.
- **O(N) Star / Host-Relay topology + direct LAN UDP data plane (`f725ab8`,
  `81e058b`, `5fec2a4`)**:
  - Above 12 players, `LANMeshNextTick` (`mods/lan/source/module/mesh_schedule.c`)
    defers client-to-client GT2 connections (`INT64_MAX`), collapsing 8,128
    $O(N^2)$ GT2 links into 127 $O(N)$ host-client links (128-console room join
    dropped from ~475 s to ~99 s).
  - `DWC_GetDirectConnectedAIDBitmap` (`0x800D21BC`) reports `DWC_GetAidBitmap()`
    for clients with a live host GT2 link while the host continues reporting its
    true direct GT2 bitmap.
  - `DWC_SendUnreliable` (`0x800E85B4`) routes all unreliable RKNet traffic over
    a dedicated non-blocking UDP `data_socket` on port `27910` directly to each
    peer's `local_ip`, with GT2's idle `receive_timeout` raised to 1 hour.
- **Lobby, Track Roulette, and 128th-place race fixes (`411c0eb`, `278558c`,
  `6aa93cd`)**:
  - Bypassed the 170 ms per-peer export gate in `RKNet::ROOMHandler::ExportROOMandUSER`
    (`0x80656BC0`), `RKNet::SELECTHandler::PrepareAndExportSELECTandUSER`
    (`0x80658FE0`), and the `RKNet::RH1Handler` export (`0x8065C714`) above 12
    players so full 128-peer sweeps finish in ~2.1 s instead of 21.6 s.
  - Eliminated the up to 70.6 s per-console roulette crawl in
    `Pages::Vote::BeforeControlUpdate` (`0x806123C8`) above 12 players by
    populating all vote cards immediately and snapping to `winningVoteControlIdx`
    once the 4.17 s deceleration spin finishes.
  - NOPped `extsb r7, r7` at `GMDataOnlineVS::HandlePositionTracking+0x19C`
    (`0x8053A100`) so 128th place (`0x80`) stays zero-extended (`128`) rather
    than sign-extending to `-128`.
  - NOPped `RaceinfoPlayer::EndLap+0x254` (`0x805300C4`) above 12 players
    (`bb5d650`) so remote online racers (`playerIdx >= 13`, beyond RH2's 13-bit
    finish-consensus mask) fall through into `RaceinfoPlayer::EndRace` when
    crossing the lap-3 finish line.
- **Runtime & Windows lab scaling (`e77f8fd`, `6f533ab`, `bf26430`, `9ac4d1c`)**:
  - Raised `SOCK_DGRAM` `SO_RCVBUF`/`SO_SNDBUF` to 2 MiB in `network_core.cpp`,
    skipped Null-backend shader prewarm in headless mode, and wired
    `MKW_SHARED_CACHE_DIR` into `mods/lan/lab/make-lab.ps1` and `run-lab.ps1`
    (capacity range raised to 128).
  - Fixed `SelectThread` in `runtime/src/hle/os/os_scheduler.cpp` to re-enter
    idle/selection when the run queue drains or a switched-to worker fiber exits.

## Verified live LAN runs (capacity 128)

| Consoles | Timing (`MKW_TEST_CLOCK_DIVISOR`) | Check | Result | Elapsed |
| --- | --- | --- | --- | ---: |
| 3 sparse (`1, 33, 128`) | Normal (`1`) | `--race` | **PASS** | 73.72 s |
| 3 sparse (`1, 33, 128`) | Normal (`1`) | `--race --finish` | **PASS** | 205.72 s |
| 33 (`1..33`, AIDs `0..32`) | Normal (`1`) | `--race` | **PASS** | 158.48 s |
| 65 (`1..65`, AIDs `0..64`) | Functional (`2`) | `--race` | **PASS** | 408.25 s |
| **128 (`1..128`, AIDs `0..127`)** | **Functional (`4`)** | **`--race`** | **PASS** | **565.68 s** |

All 992 `Translator.Tests` and all 30 LAN Python unit tests pass with zero
regressions.
