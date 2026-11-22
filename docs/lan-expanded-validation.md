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

The first normal-timing traced comparison with VI coalescing (`a2c447b`)
reached eight members but failed admission on console 15's error dialog at
89.72 seconds. The longest host advertisement gap was still 32.01 seconds.
This remains a failed qualification, not a lobby speed/reliability pass.

Discovery previously advertised once per sixty network-thread polls and aged
peers/requests by that same poll count. That equates to one second only when
the thread actually runs at sixty polls per second. Discovery now derives its
sixtieth-second timestamp from `OSGetTime`, sends at most one advertisement
when due, and schedules the next one relative to that send. A stalled poll
does not generate a catch-up burst. Discovery retains a five-second lifetime;
queued expanded-room reservations instead retain the existing maximum SDK wait
allowance of sixty seconds, so a request waiting behind other reservations is
not discarded merely because its discovery advertisement expired. Stock
capacity retains the five-second request lifetime. Native tests cover slow/fast
polls, tick rollover, independent queue/discovery expiry and queue ordering.
Live admission and race validation remain pending.

The SDK-clock build (`d0d90a1`) still failed its 24-console attempt during
admission at 98.79 seconds. In native traces, console 8 sent its reservation
ACK at monotonic time 114621.007, but the host did not read it until
114653.792. The host had already cancelled that reservation. This identifies
delayed control-socket service even after advertising is based on SDK time.

A subsequent thirteen-console capture identifies the login/matching caller
at priority 17 and the LAN service at priority 24, frequently ready on the run
queue. Read these priorities from the SDK thread offsets in captured RAM:
the initial new diagnostic printed zero because the imported `OSThread`
declaration used a four-byte context-flags bitfield and incorrect padding.
Those declarations now match the SDK, with PowerPC compile-time checks for
context/thread fields and the complete `0x318`-byte allocation.

Expanded builds now create the LAN service at its SDK login caller's priority
when that priority is valid and more urgent than the old service priority.
It shares the observed matching caller's priority 17 without preempting that
caller on priority alone. Stock capacity, a missing caller and unsupported
priorities retain priority 24. Policy regressions pass; repeated live admission
and race checks are still required to qualify this scheduling change.

With `fd10c82`, thirteen consoles completed SDK connectivity at 33.30 seconds
and reached race creation/countdown. The run failed at 181.67 seconds as racing
started, with an unmapped read reported on console 2. Captured RAM identifies
player ID 12/AID 12 and its selected RACEDATA payload at `901f52fc`.
`PacketMgr::GetRacedata` (`8064F634`) still searched only twelve map entries:
the four-entry loop's count at `8064F65C` remained three. For the thirteenth
player, its ordinal stayed minus one, returning `901f52bc`, 64 bytes before
the payload. That memory contains allocator metadata. Decoding it as remote
kart state triggered invalid actions, including cannon entry on Luigi Circuit.
The earlier previous-rank heap failure did not recur before this distinct fault.

The new native getter regression reproduces exactly the minus-64-byte result
at thirteen players. Its checked loop count now uses `ceil(players/4)` while
retaining the existing guarded map loads and authored 64-byte local-player
payload stride. Packet-manager tests pass at 12, 13, 24, 32, 48 and 100
capacity, covering both receive buffers, unique/shared console mappings and
sparse high-index players. The complete layout-consumer audit also passes.
These fixtures validate storage/getter behavior, not playable networking above
the SDK console/local-player limits. Live race revalidation remains pending.

The first combined `8168076` 24-console attempt reached eleven visible members
at 45.95 seconds, then failed on a reservation error at 90.32 seconds. It did
not reach a race. The host's final SYN barrier lacked ACKs from AIDs 9 and 10.
Console 21 (AID 10) received the host SYN while in `CL_GT2`; console 22 (AID 9)
received it while accepting a peer in `SV_OWN_NN`. Both later returned to
`CL_WAITING`, but the original handler had already ignored the SYN. This shows
why draining queued controls only in `CL_WAIT_RESV` is insufficient.

Expanded clients now retain one owned host SYN during the observed client/peer
negotiation states. A bounded queued-control drain remains the fast path. If
cancellation is still in flight, the original SDK handler receives the retained
payload only after the real state transition to `CL_WAITING`. No state or ACK is
invented. Retention is scoped to the same SDK instance, host PID and AID, expires
within the SDK's member-scaled SYN wait, and clears on room lifecycle changes or
barrier completion. The pinned parser reads eight bytes for flag one and 32 bytes
otherwise; only that payload is copied, never its transport-owned pointer.
Native regressions cover every supported negotiation state, both payload forms,
payload reuse, re-entry, queued/in-flight cancellation, expiry and host/context
changes. Stock builds retain the original handler path. Live revalidation remains
required.

With `e24ceaa`, all 24 consoles held room membership for five seconds at
102.07 seconds under normal timing. The original SDK processed deferred SYNs
after cancellation on consoles 11 and 22. Full peer connectivity still grew
slowly: summed connection-mask bits reached 174 at 180 seconds and remained at
174 through 270 seconds. Captured reservation targets show explicit cycles
16-to-24-to-16 and 7-to-12-to-7. At the final observation (510.91 seconds),
255 of the required 576 per-console connection bits were set. The owned
diagnostic driver was then interrupted; it did not qualify complete SDK
connectivity or reach a race.

The thirty-second reverse-initiation fallback allowed those cycles to form.
Peer scheduling now assigns each link solely to its lower-PID endpoint, so
outgoing reservations strictly increase PID and cannot form a cycle. The
passive endpoint receives a positive `INT64_MAX` deadline, consistent with the
SDK's signed time comparison, rather than an expiring grace period or negative
sentinel. Active retries keep their bounded jitter/backoff and original SDK
attempt/connection/teardown handling. Stock capacity and unsupported states keep
the original scheduler. Native pair/ABI checks pass; live comparison is pending.

The first strict-ownership attempt (`2c12a20`) failed admission on console 20's
error dialog at 91.28 seconds. The host received fourteen of fifteen SYN ACKs;
console 11/AID 8 retained the remaining SYN while waiting on a peer reservation.
Its control log shows the next admission's stop-mesh command followed by the
previous barrier's resume-mesh command, then a new outgoing reservation and the
host SYN. Retaining that SYN cannot finish the barrier when the cancellation
has already been undone by the late resume.

The host could dispatch another reservation from the background thread after
the SDK published `SV_WAITING` but before the previous `dwc_think` call finished
its post-barrier socket writes. Guest I/O can yield between those steps even at
equal thread priority. Expanded builds now process control packets, queued
reservations, SYN replay and reservation budgets at the SDK thread's complete
`dwc_think` boundaries. The background thread serves discovery and telemetry.
An initialization-ready flag excludes unbound/blocking startup sockets; a shared
poll guard bounds callback re-entry. Stock-capacity builds keep their previous
service path. A native scheduling fixture yields after publishing idle and
checks that the old resume precedes the next stop, with initialization, shutdown
and re-entry guards. Live comparison and the rebuilt hook's linked ABI still
require validation.

With the SDK-thread serialization build (`be5eb75`), a normal-timing 24-console
attempt reached all room members and complete SDK connectivity at 63.09 seconds.
The 60.15-second snapshot already has all 576 connection-mask bits set, every
client in `CL_WAITING`, the host in `SV_WAITING`, and no suspension. All 25 LAN
tests, including the linked thirteen-hook ABI, passed before this run. This is
one full-connectivity pass; repeated joins, racing, results and reconnection
remain separate qualifications.

The input driver missed its short initial left pulse and opened the chat menu.
The running test was navigated back to the room and into Start using ordinary
controller input, with the original Start-description/callback confirming the
selection. Only the input driver was paused during this correction; game RAM
was not modified. All 24 consoles subsequently entered course selection and
race loading. The run failed at 1334.03 seconds when console 23 crashed while
building its results rows. It had lost remote connections during loading and
finished its remaining single-player field at frame 241 without completing a
lap. This is a connectivity pass followed by a race failure, not a completed
24-player race or an unattended race-start timing benchmark.

The results crash used HUD slot `-1` for local player 13. The real screen
initializer still scanned twelve players and retained five accesses to the old
HUD-map location. A native thirteen-player fixture reproduces the fault; the
expanded initializer, relocated map and bounded spectator fallback now pass
12/13/24/32/48/100 fixtures. An independent outgoing RACEDATA fixture also
reproduces a high-slot buffer underrun: its separate twelve-player ordinal
search leaves `-1`, copying a 64-byte packet before the packet-manager object.
Correcting that checked count requires live revalidation before attributing the
loading disconnects to the underrun.

A same-binary, normal-timing two-console control completed three laps and
finished both racers on both peers at 211.98 seconds. The CPU-driving hook did
not cause an immediate finish in that baseline. This does not qualify the
24-player race path.

The `8559287` revalidation failed admission at 96.00 seconds, with thirteen
room members and the host waiting for AID 6's SYN ACK. Console 2/AID 6 received
the next node's UDP notification while still in `CL_SYN` for the preceding
admission. The preceding reliable SYN-2 then completed, resumed mesh creation,
and started another outgoing reservation to console 22. Its next host SYN was
retained in `CL_WAIT_RESV` indefinitely. SDK-thread ownership alone does not
order delivery across UDP controls and reliable GT2 SYN packets.

Expanded clients now retain an owned next-node notification received during
`CL_SYN` and apply it immediately after the preceding original SYN-2 handler
returns to `CL_WAITING`, before the SDK can create another peer reservation.
The control remains scoped to its SDK instance and host, expires, and clears
on room lifecycle changes. The original status/SYN handlers still perform all
state changes and ACK generation. Native regressions reproduce the overwritten
stop before correction, then cover buffer reuse, callback order, stale contexts,
expiry, unrelated callbacks, stock builds and malformed lengths. This requires
another live admission/race qualification.

The next normal-timing run (`264857b`) completed all 24 SDK connections at
226.10 seconds. The first 23 consoles already had all 529 connection bits;
console 14 was still advancing through startup pages until 184.45 seconds.
All 576 bits then remained present through course voting. The longer one-second
menu pulse selected Start without manual intervention. Production logs show the
new retained-node replay being exercised on eleven consoles.

Race qualification failed at 1350.25 seconds on console 18's error dialog.
Several consoles had lost the host during loading and elected themselves as
one-member servers. The previous HUD animation crash did not recur, and the
captured console-18 heap lists validate. This separates the proven screen and
export-buffer fixes from the still unresolved loading disconnects.

A loading snapshot shows SDK owners ready at priority 17 behind main/render
work at priority 16; the owner is at the LAN shim's timed sleep return. The
expanded login path now raises only that observed priority-17 caller to 15.
Its existing 16.666-millisecond timed sleep remains in place, and the discovery
service continues to share its caller's priority. Other caller priorities and
stock builds retain their behavior. Native policy coverage checks the exact
caller guard, repeated calls, absent owners and stock behavior. This scheduling
candidate still requires live race/connection validation; the snapshot alone
does not establish it as the cause or complete fix for the disconnects.

With `698297f`, thirteen normal-timing consoles completed SDK connectivity at
32.02 seconds and reached race completion on every peer at 775.39 seconds.
All thirteen peers reported thirteen finished racers, with minimum lap 3 and
maximum lap 4, on Luigi Circuit in online VS mode. All 169 connection bits
remained present; no server-loss or guest-fault reports occurred. The original
HUD getter assigns slot zero to every peer's own local player, including player
ID 12. This is one CPU-driven thirteen-console finish pass in the 24-capacity
build, not a completed 24-player race, repeated-join benchmark, results-screen
qualification or reconnection pass. The exact per-peer observations are retained
in [the validation record](validation/lan-race-rmce01-13.json).

## Controller departures and the remaining SDK limit

The controller now records racer and console departures in separate packed
fields. Native fixtures run the original callback through console ID 127 and
check the packet holders, mapped racers, word boundaries and resets.

A Linux 24-slot rebuild containing the racer-mask change (`b11b861`) completed
two-peer SDK connection in both the headless and visible runners at normal
timing. Both headless peers advanced on Luigi Circuit (online VS, mode 7) and
reached lap three. The longer 680-second run ended before either reported a
finish. Its final samples showed 7,803 host frames and 8,649 client frames.

The same executable subsequently completed the two-peer finish check at normal
timing. SDK mesh readiness was observed at 60.79 seconds. At 810.42 seconds,
both peers reported both racers finished on Luigi Circuit after three laps.
The host observed frame 7,988 at the finish checkpoint and the client observed
frame 8,708. The executable SHA-256 was
`5199be02eeadedab044db87c274904bb9e07d44c92eb031d2387ca67f0e618bf`.
LAN qualification still needs larger fields, results screens and reconnection.

In the verified USA executable, `DWC_GetAidList` (`800D211C`) clears and fills a
32-byte static list at `802ED160`. `DWC_GetDirectConnectedAIDBitmap` (`800D21BC`)
scans the 32-entry connection table at `802ECFE0` and returns one word.
The direct relative branches in the verified executable sections resolve to
these call sites.

| Getter | Sites | Caller |
| --- | --- | --- |
| `DWC_GetAidList` (`800D211C`) | `800E88DC` | `DWCi_TransportProcess` |
| `DWC_GetDirectConnectedAIDBitmap` (`800D21BC`) | `806549EC` | `RKNet::Controller::UpdateSubsAndVR` |
| `DWC_GetAidBitmap` (`800D2414`) | `80654988` | `RKNet::Controller::UpdateSubsAndVR` |
| `DWC_GetAidBitmap` | `800D496C`, `800D49C8`, `800D49DC` | `DWC_GetSuspendMatch` |
| `DWC_GetAidBitmap` | `800D7BDC`, `800DA730` | Match processing and match polling |
| `DWC_GetAidBitmap` | `800E6920`, `800E6DC0` | Server-down queries and server polling |

The direct-connected bitmap getter also inlines the same 32-byte AID list.
Widening the controller's departure flags leaves both lists, the SDK connection
table, the returned masks and the inlined consumers at their original limits.
The LAN build guard remains at 32 capacity slots until these paths can use
wider console state consistently.

The SDK transport owns a second per-AID array, distinct from the connection
table. The original friends-match allocation embeds it at `0xC20`, with 32
64-byte records followed by four callbacks, a halfword send-split limit and a
receive timeout. Relocations cover the outer allocation, both clears, the
receive callback's timestamp bound and the trailer accesses. The allocation
retains the original 32 records through capacity 32 and grows in groups of 32.

Original-code fixtures at 12, 32, 33, 65, 97 and 128 test storage, initialization,
callbacks, receive-buffer setup, timestamp updates, clearing a connection and
shutdown. The 128-slot fixture reaches AID 127 and verifies guard words,
including where an out-of-range AID would have stamped a timestamp. The
unreliable forwarding loop still reads a one-word recipient mask. That loop
and the matchmaking connection tables still limit live LAN capacity.

The LAN discovery cache and pending reservation table now size to the configured
capacity, with a floor of 32 entries. A compiled fixture exercises the module's
actual endpoint registration and both PID lookup paths at capacities 12, 32,
33 and 128. It fills every slot, verifies lookup in slot 127 at capacity 128 and
checks that an extra endpoint leaves the full cache intact. All 27 LAN tests
passed with rebuilt 12- and 24-slot modules, and the 24-slot linked payload is
byte-identical to the previous build. This storage is used before SDK AID
assignment. The live LAN build limit remains 32.

The SDK's 32 connection pointers at `802ECFE0`, the paired 8-byte connection
metadata records at `802ED060` and the AID list at `802ED160` now use one checked
layout. Through capacity 32, they retain their original addresses and sizes.
Above 32, they grow together in groups of 32 and occupy reserved space before
the MEM2 arena. Relocations cover the original bases, clear lengths, connection
lookup loops and unrolled free-slot scans. Original-code fixtures at 12, 32, 33
and 128 cover a server reset, final-slot allocation and lookup, AID enumeration,
smaller-list reload and guard words around the relocated tables.

The SDK matchmaking node array is independent of these connection tables.
Matchmaking still needs expanded node storage and connection/suspend mask
consumers still need wider words before the live LAN cap can rise.
