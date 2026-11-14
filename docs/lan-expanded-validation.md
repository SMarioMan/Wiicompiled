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
