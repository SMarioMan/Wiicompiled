# Expanded LAN validation checkpoint

These runs use the USA disc, capacity 24, independent portable saves and
loopback addresses on a shared four-CPU Linux host. The headless renderer is
null. Wall times start when the driver starts; they include startup and menu
navigation. Process survival alone does not count as a race pass.

| Source | Consoles | Timing | Full SDK connectivity | Race progress |
| --- | ---: | --- | ---: | --- |
| `f9589e0` | 2 (IDs 1 and 24) | Normal | 22.88 s | Not requested |
| `f9589e0` | 12 | Normal | 34.25 s | Not requested |
| `edccb59` | 2 (IDs 1 and 24) | Normal | 25.17 s | Pass at 77.90 s |
| `edccb59` | 12 | Normal | 33.05 s | Pass at 198.97 s |
| `edccb59` | 24, simultaneous arrivals | Normal | Failed reservation negotiation | Not reached |
| `edccb59` | 24, one-second startup gaps | Normal | Failed reservation negotiation | Not reached |

The advancing-race check requires every peer to report the same course, actual
player count and running stage, then advance by at least 120 simulation frames.
The twelve-console run holds room membership for five seconds at 31.81 s before
confirming full SDK connectivity at 33.05 s. All twelve peers subsequently
advance in the same Luigi Circuit private VS race (mode 7).

The `edccb59` executable SHA-256 is
`033631a32ba5d7a766b2f3eddb3ab1a38622e24b7830babc9b32259151e583d3`.
It includes the migrated vote/rating/selection pages, their generated retail-style
cards and the fair reservation queue. A preceding live vote crash exposed stale
cross-page selection-count/Mii references; native submission regressions now
cover full and partial lobbies, and the live two-/twelve-console transitions pass.

The two 24-console runs were stopped after clients reached error page `0x4d`;
they are failed attempts, not successful benchmarks. Their logs include SDK
reservation timeouts. Staggering startup by one second did not resolve them.
These observations do not isolate the cause between scheduler load and protocol
behavior. Normal-timing 24-console joining remains an open release requirement.

The fair queue has native regression coverage for preserved arrival age,
duplicates, idle-arrival bypass, crossed client negotiations, malformed packets
and teardown. The measured runs above are not a controlled statistical speed
comparison with the old queue. Repeated matched comparisons, late joins,
rejoining, complete races, results and a second race remain required.

Clock-scaled runs must be reported separately as functional tests. They cannot
qualify normal-timing connection reliability or real-time performance. Tests
through 100 slots establish individual storage/algorithm behavior; the current
LAN protocol still limits console masks to 32.

With source `10a1357`, opt-in local CPU driving and a prototype finish driver,
both real LAN racers completed all three Luigi Circuit laps at normal timing.
Both consoles reported `observed=2 finished=2 stage=4` at 207.10 seconds. The
executable SHA-256 was
`8bd4914e77092ae9ca96b130f724958991b1d264fd76d02fb8e3e5bf99b71d47`.
The CPU driver preserves network player ownership and race RNG. The reusable
`--finish` driver checks this simulation evidence; results and a second race
remain separate requirements.

With source `b4706a8`, the live-peer reservation allowance was active: a failed
normal-timing client logged a 31.316-second reservation wait. That 24-console
attempt still failed and does not establish reliable normal-timing joining.
In a separate clock-divisor-16 functional attempt, all 24 consoles held room
membership at 50.50 seconds and complete SDK connectivity at 187.45 seconds.
They voted and entered the race scene, where telemetry and RAM snapshots showed
only twelve active race/menu participants. The run was stopped as failed;
controller AID-map copies, subsequently fixed in `b2fd63f`, had left the last
twelve entries inactive. No 24-player race/finish pass is claimed from this run.

Source `392fee8` passed normal-timing complete-connectivity checks with three
consoles (sparse IDs 1/13/24, 24.12 seconds) and thirteen consoles (33.93 seconds).
Its simultaneous 24-console attempt failed during admission below thirteen
members. The expanded-build scheduler now applies from three participants, so
it also covers that initial growth; stock twelve-capacity builds retain the
original scheduler. This adjustment still requires live revalidation.

The early-growth scheduler initially exposed crossed reservation waits and then
status replies to SDK-introduced peers absent from multicast discovery. The
corrected candidate learns validated incoming endpoints, uses SDK private
addresses instead of the shared public placeholder, and gives the preferred
pair initiator a longer grace period. Its normal-timing thirteen-console run
held room membership at 35.17 seconds and completed all peer links at 37.40
seconds. Binary SHA-256:
`26991633eefefb4bf7a3d78087653f0350ce6073ce18d74e59adbb07d42c978a`.
This is a connectivity pass, with no race/finish claim. Earlier failed candidates
remain failed evidence; timings do not establish an isolated statistical speedup.

## Thirteen-player online position tracking

With source `ea947ef`, thirteen consoles completed the normal-timing SDK mesh
in 35.67 seconds and race setup reported thirteen active participants. The
race then failed inside `GMDataOnlineVS::HandlePositionTracking` (`80539F64`),
which retained twelve-entry stack arrays for IDs, ranking scores and timers.
The executable SHA-256 was
`26991633eefefb4bf7a3d78087653f0350ce6073ce18d74e59adbb07d42c978a`.
This is a failed race qualification, not a thirteen-player race pass.

The online tracker now declares a capacity-sized frame, score initialization
count, timer constructor/destructor counts, array addresses and floating-point
conversion scratch offsets. Timer stride remains twelve **bytes**. The original
twelve seed IDs remain valid: the tracker overwrites every active ID before
sorting. Native tests execute the original online and offline tracker bodies
with live, finished, mixed and tied fields at 12, 13, 24, 48 and 100 capacity.
They check final order, rank uniqueness, saved registers and frame/output guards.
Before the migration, the new thirteen-player online test terminated with a
memory fault; the original offline test already passed. Live race revalidation
is still required.

The subsequent `9603cfa` live attempt still faulted and was stopped at 209.60
seconds. RAM showed slot thirteen's previous finish rank was zero in both
scenarios before sorting. Two unrolled scenario helpers (`80529B20`, reset;
`80529C28`, advance) had neither expanded their twelve records nor relocated
their settings writes. The online VS factory also allocated/constructed only
twelve records, with later racers overlapping its fixed tail. These are
additional migration gaps; the scratch-frame fix alone does not qualify racing.
The same source's separate normal-timing 24-console lobby attempt failed during
admission at 74.20 seconds. Binary SHA-256:
`f95f3a7b68e17e202f19aae7366515cbecb5e8812774a3bf8b33d05a7697bd65`.

The online VS object now grows its twenty-byte participant records and moves
its fixed tail, including embedded timer/range objects and local-player IDs.
The scenario reset/advance helpers retain their original twelve records and
register behavior, extend the same score/rank operations to later slots, and
relocate their settings writes. Native lifecycle tests pass at 12, 13, 24, 32,
48 and 100 capacity, checking allocation size, every timer construction,
one-/two-local-player initialization, next-race rank/point transfer, race-number
wrap, untouched player fields and object/scenario guards.

With `2c63ebe`, another simultaneous thirteen-console attempt failed admission
at 43.26 seconds; it did not reach race validation. A separate normal-timing
sequential 24-console attempt admitted twenty members at 447.06 seconds, then
failed at 699.04 seconds during client 21 startup as established members lost
connections. A five-second thread CPU sample across 21 processes totaled about
372.5% CPU on a four-CPU host; cgroup memory use was about 11 GiB of 32 GiB with
no OOM kills. These observations show substantial CPU load, but do not isolate
the disconnect cause. The executable SHA-256 was
`bbeb85f024b039c73d3c04f7bb4dbe5325f40b10e7e88174f33eaa32b5e67512`.
Sequential startup now rejects an error dialog from any established peer, not
only the client currently joining. Normal-timing 24-console admission remains
unqualified.

The failed thirteen-console admission includes a client waiting 11.115 SDK
seconds while the host queued its request behind the current SYN barrier. The
old allowance counted only peers beyond twelve, giving that field just eight
seconds. Expanded builds now budget for every other live or SDK-known peer,
including early growth: six seconds plus two per contender, capped at sixty
seconds below the existing friend-room join watchdog. Stock twelve-capacity
builds retain six seconds. Policy tests cover the cap, discovery expiry,
SDK-known peers, special timers, other states and unchanged origin/retry fields.
Live comparison with this revised allowance remains pending; it does not
establish a speed improvement or resolve the observed large-room stalls.

The revised deadline was observed in a subsequent `9a5f5ed` thirteen-console
attempt: failed clients waited about thirty seconds, but admission still failed
at 65.72 seconds. All thirteen guest RAM regions were captured for diagnosis.
The host was in `SV_SYN` with seven members, waiting for AID 3 (console 3).
That client logged SYN-0 while in `CL_WAIT_RESV`, followed by the host's
stop-mesh control message returning it to `CL_WAITING`, with no SYN-1 ACK.
The original SYN handler (`800D96B4`) accepts SYN-0 only in `CL_WAITING`; the
host's original SYN timeout scales as five seconds times its member count.
This ordering race explains the observed missing ACK and long barrier stall.
Binary SHA-256: `2659795c8f98577bbcf6694339aad9b39292a420df4be43db8802505aa4aede5`.

Expanded builds now drain an already-queued control-socket batch before the
original SYN handler checks its state, scoped to a host SYN-0 received by a
client waiting on a reservation. Original SDK handlers retain state changes,
validation, payload lifetime and ACK generation. A re-entry guard bounds nested
callbacks; stock capacity and other packets/states keep their original path.
This addresses a queued cancellation arriving through the other receive loop;
it does not guarantee ordering of datagrams still in flight. Native ordering
fixtures cover cancellation present/absent, re-entry and all guard conditions;
linked-module checks pin the actual SDK entry and retained trampoline. Live
revalidation remains required.

With `cce0293`, thirteen consoles completed room membership and full SDK
connectivity at 45.91 seconds under normal timing with RAM capture on every
console. The race transition still failed at 269.17 seconds. Console 8's RAM
identified the damaged heap link: player thirteen had previous rank zero, so
the race-info constructor's order-array write at `8052DDFC` targeted `ids[-1]`.
Its player ID byte changed the allocator's next-block pointer immediately
before that array. Later heap accounting in `GameScene::enter` followed the
broken list. Binary SHA-256:
`174ef8501882241f8e558826f5526b1fd51c09c0bb5265f7eeb9530eb921849d`.
A selection-phase sample had valid used-block lists on all thirteen consoles.
This is a connectivity pass and a failed race qualification.

The isolated reset regression confirms `Racedata::ResetScenarios` already
initializes expanded menu/award ranks correctly. The later online compactor,
`RacedataScenario::UpdateFromPrevRace` (`8052A698`), still used a twelve-byte
scratch array and twelve-slot scans/writeback. A native thirteen-player fixture
with cleared previous ranks reproduces its unchanged zero in slot thirteen.
The expanded wrapper now snapshots the original ranks, retains the retail
helper's register/unrelated effects, and produces its stable ranking order over
the full capacity: positive eligible ranks first, then zero ranks in slot order.
Inactive and out-of-range ranks retain the retail identity fallback. Modes
outside the original online range and stock capacity retain the original path.
Native cases also cover reversed/tied ranks, sparse inactive slots, unrelated
bytes, and the independent menu/award reset before live revalidation.

At `5e453ca`, a normal-timing 24-console attempt failed admission after
105.87 seconds, with the host reaching twelve members. Its remaining queued
reservations were still within the discovery lifetime; extending queue expiry
would not address that capture. A later traced repeat failed on an error dialog
at 176.96 seconds. Native UDP tracing measured a 47.28-second gap between host
advertisements and up to 33,280 bytes waiting in the host's control socket,
without any new UDP receive-buffer errors or drops. A traced two-console control
completed room membership and SDK connectivity at 24.43 seconds.

The VI poll previously replayed old video boundaries after a host stall, retaining
the remaining backlog for subsequent polls. It now coalesces a pending interrupt
at the latest due boundary, preserving the display phase and returning after one
callback. This avoids replaying an unbounded history of missed video interrupts
and repeatedly waking higher-priority threads before lower-priority network work.
The production-poll regression fails before this change and passes afterward,
covering a five-second stall, a slow callback, phase preservation, early/repeated
polls and disabled/uninitialized polling. Its effect on actual 24-console lobby
progress still requires a live comparison; the trace alone does not establish
that the VI backlog caused every connection failure.
