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
