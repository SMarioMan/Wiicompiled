# 128-player LAN handoff

The Linux 128-capacity build has completed offline VS, Team VS, Grand Prix,
Balloon Battle, Coin Runners and Time Trial validation. Time Trial retains
its single-racer rules. The live LAN build rejects capacities above 32. Live
LAN testing has observed 13 peers finish a race, and a later two-peer
regression run finished on both peers. See [offline results](128-racer-comparison.md)
and [LAN results](lan-expanded-validation.md).

## Published code

- `3b1ac4b` grows the SDK matchmaking node list in groups of 32 through
  AID 127. The native translated fixtures initialize the list, exhaust free IDs,
  look up the last node by AID, PID and index, compact the list, and clear it
  with guards around the allocation. The relocation audit checks 1,414
  executable operands. A full translation emitted 29,456 functions.
- `3193efc` makes LAN address overrides and mesh scheduling use the expanded
  matchmaking node count. The host fixtures cover entries through index 127
  and check that inactive and out-of-range entries are ignored.
- Static connection pointers, connection metadata, transport records and
  one-recipient forwarding already have translated-code fixtures above AID 31.
  Authored LAN branches use symbols. Original guest executable addresses
  remain the stable reference points for translation and patches.

## Remaining LAN work

The SDK tracks suspend votes in the word at `DWCMatch+0x1c`. The primary
paths to widen together are:

- `800D3894/800D38A0` clear departed peers. `800D7BE0/800D7BEC`
  update vote state while processing the match. `800DB344` resets it.
- `800D4968` reads it in `DWC_GetSuspendMatch`. It intersects votes
  with membership, conditionally excludes the local AID from the vote,
  and keeps the last settled state while votes are mixed.
- `800E6F44–800E6F7C` tests whether an individual vote changed.
  `800E7048` reads the mask for transmission. One sender path for
  command `0x82` uses four words when broadcasting a state change
  (`800E715C–800E7194`). Review the separate path at `800E71DC`.
  The receiver commits the fourth word at `800DE584`. Extend both
  directions and validate packet lengths before reading additional words.

The SDK membership and direct-connection getters also return one word.
Their audited callers are listed in the LAN results. In the game, start
with `RKNet::Controller::UpdateSubsAndVR` at `80654988` and `806549EC`
and follow the masks at `RKNetSub+0x10/+0x14/+0x18` into room controls,
race startup, departure and synchronization. The LAN deferred-SYN path
copies either two or eight words according to the pinned original protocol.
Coordinate any size change with that buffer and its replay path.

The first live boundary case is 33 independent consoles. Check that AID 32
can join, vote, race, finish, return to the room and rejoin without changing
AID 0's state. Repeat across 63/64 and 95/96, then exercise a 128-console
room. Keep the build guard until the room and race paths support the
expanded field.

Use the SMarioMan commit identity and the requested
`Co-Authored-By: GPT-6.2 Sol (Light) <codex@users.noreply.github.com>` trailer
for subsequent commits.
