# Vanilla 128-racer comparison and implementation record

The active target is vanilla RMCE01, primarily offline, at 128 racers. Preserve
capacity-aware layouts, checked operand relocations, translated guest patches
and narrow native wrappers. Existing LAN work remains WIP; 128-player networking,
Retro Rewind, Item Fever and other mod-pack data are outside this investigation.
The ultimate target remains up to **128 console multiplayer**. Keep racer capacity,
console capacity and network-mask width separate; offline qualification is the
current milestone and does not qualify the later console-networking work.

Reference: [MKW100](https://github.com/vinichenzo279-wq/mkw100), inspected commit
`b96f094e7850c9d672daf6b82f9d0c46af6f6d59`. Its PAL addresses identify discovery
leads. They are not NTSC-U patch addresses. Correspondence must use the region
chunk map, function identity and original instruction evidence.

The reference uses shadow windows and native replacement tables. Those mechanisms
are not being imported. Similar protection in this branch can be implemented by
expanded inline storage, moved trailing fields and checked original consumers.

| Vanilla field/resource and original failure | Current classification and evidence | Smallest next change or verification | Required evidence |
| --- | --- | --- | --- |
| ArchiveMgr: two `ArchivesHolder[12]` arrays, offsets `0x8`/`0x158`, stride `0x1C`; overflow reaches the other array and content archives. | **Partially covered after migration.** `ArchiveManager` now models both racer arrays and retained content, request, task, course-loader and worker fields. Five explicit allocation/count operands and 188 audited field operands relocate the USA factory, construction/destruction, static request dispatch and known external consumers. A live startup failure exposed nineteen missing operands; the external singleton inventory now guards them. | Retain this typed migration. Exercise real mixed-resource loading and audit external/indirect consumers with live heap instrumentation; fixture callbacks currently stand in for archive I/O and resource constructors. | Passed original USA factory, array constructor/destructor walks, both indexed holder/file lookup families and retained content state at all 13 required capacities, with strict auditing and two lifecycles. Derived 128 layout: second array `0xE08`, size `0x1F7C`. The rebuilt 128-capacity game completes one-lap Luigi Circuit VS probes at 13 and 24 active racers with native finish flags for every observed racer and no reported hits in the exercised watches. Full roster diversity, other modes and race-to-menu-to-race transitions remain unqualified. |
| Balloon manager: `10*N` entries, `N` held-balloon records and change flags overwrite adjacent state. | **Already covered for tested storage paths through 128 active.** `BattleBalloonManager` retains its relocated pool and per-player records. Full-field fixtures now check 1,280 pool entries at 128, construction/reset, preferred/fallback assignment, release/transfer, calc/model loops and kart cleanup; rendered objects are callbacks. | Retain this relocated pool. Qualify actual rendered object resources, score/state update paths, battle completion and scene teardown separately. | Full active fields at 25/26/48/52/64/128, allocation guards, ownership and lifecycle. |
| Balloon counts: byte fields `N`, `5*N`, `10*N`; pool totals wrap at 26/52 racers. | **Partially covered after checked width migration.** The four-byte header now names its byte fields without changing ABI. Nine known count reads derive full-width `5*N`/`10*N` values from the player byte, pool masks/scans widen, and the five selected pool ordinals use bounded halfwords. Full active-count fixtures pass all required boundaries. | The independent unchanged-field inventory checks all discovered literal-global/manager count reads, including kart cleanup, with expected words/factors. Continue live/indirect and balloon-object legacy-byte consumer audits; no shadow pool was introduced. | Passed 1,280 distinct pool pointers, both selection paths above index 255, release/transfer, resets, update/model and cleanup loops, manager destruction and register/memory guards. Actual object construction/rendering and complete battle lifecycle remain unqualified. |
| Effect creator namespace and EGG group-slot table. | **Partially covered; allocation/registration now verified through 128.** Existing count/role relocations request `N+4` creators. Real EGG constructor/table, creator registration and reserved-role initialization fixtures pass at 127/128 (132 creators at 128). Our reserved-role model differs intentionally from MKW100's `N+2` racer-group minimum. | Verify at least 132 creators and 130 usable racer EGG groups at 128; retain larger reserved-role capacity where required by our model. Audit lookup/destruction and boot-to-race lifetime. | Original table allocation/initialization and high-ID effects at 127, including reserved roles and second race. |
| NW4R activity lists: four per EGG group, record stride `0x1C`. | **Already covered for backing allocation/initialization.** New `EffectActivityStorageTests` executes USA `802246C8`, its actual internal allocator and pool-header constructors, NW4R `800270A0`, activity constructors and list initialization at all required counts. At 128 it initializes 528 activity entries from the existing 132-group model. | No allocation rewrite is needed. Qualify nonempty particle/effect pools, high-ID gameplay lookups/draws/destruction and actual scene heap ownership; the new backing fixture configures other resource pools empty. | Passed count propagation, computed arena allocation, actual record construction/iteration, both list link offsets, final-racer group bounds and allocation guards over two fresh initialization cycles. Racer 127 live effects/destruction remain unqualified. |
| Light-set pool: two requests per kart plus items; signed-byte light IDs cap safe expansion. | **Partially covered after ownership fix.** Verified USA acquire `8055D018` returns null on an empty available list; `LoadBLIGHT` at `8055D608` narrows `index+8` through a signed byte. Narrow wrappers now share acquired lights on expanded-race exhaustion and defer the original release until the final owner. | Keep original pool/ID limits. Per-holder references survive other live holder construction and reset on that holder's lifetime. Qualify actual mixed-roster rendering and scene resource loading; shared light parameters reflect their last updater. | Passed real USA acquire/release and NW4R lists at every required active count, two concurrent holders, shuffled final-owner releases, discarded-owner teardown and second lifetime. Stock acquisition independently returns null after 120 slots; original 12-racer exhaustion remains unchanged. Live rendering/resources remain unqualified. |
| Coin Runners nested update scratch and signed-16-bit displacement threshold. | **Already covered for tested stack lowering.** `BattleCoinUpdateFrame` grows its nested arrays, save areas and complete frame. Real translated native fixtures now pass at 116/118/127/128 as well as 12/13/24/48/100. The fixture's fixed 32 KiB expected-data buffer was replaced with generated frame-sized storage. | Retain the current full-width IR lowering. Extend active-count gameplay coverage independently: these storage fixtures still cap active racers at 24. | Passed actual translated prologue/epilogue, scratch initialization/inverse indexes, saved registers, caller/stack guards at 116/118/127/128. Full 128-active gameplay remains unqualified. |
| Battle rank scratch: twelve byte IDs and twelve float scores in `GMDataBattle::HandlePositionTracking`. | **Confirmed missing, now migrated.** USA `805338D0` retained a `0x90` stack frame, with IDs at `+8`, scores at `+0x18`, conversion scratch at `+0x48` and saved registers above. The first real 128-active Coin Runners update overwrote saved registers/return addresses after the sound-heap fix let loading finish. | Model `BattlePositionTrackingFrame`; relocate all 38 stack operands and protect all nine one-based position conversions, including the unrolled path. Preserve original sorting and tie order. | A pre-fix native 128 regression fails. Fixed original-function tests pass all thirteen required capacities, with strict stack auditing; 128 rotates prior rankings through every conversion lane over 64 audited lifetimes. Rebuilt live battle qualification remains open. |
| Race-scene heap budgets and cross-heap ownership. | **Partially covered.** Checked `HeapRoutes` retain separate backing owners; the NDEV reserve shrinks with capacity. Real 24-player scene round trips exist; the original root partitioner passes guarded ownership checks at all thirteen required capacities through 128. This differs intentionally from MKW100's overflow arena. | Preserve routing/reserves. Measure mixed-resource peaks at 128 and verify every allocation/free/resize/reset owner. A live 128-active Coin Runners startup exhausted the MEM2 scene heap; the identified battle sound-heap request now tries the retained MEM1 backing owner using the original child factory, with original-parent retry on failure. Validate this narrow route live before claiming the failure resolved. | Effect/item-heavy races, mixed characters/vehicles, scene exit, second race and owner/heap guard reports. |
| Audio actor sorting scratch: two 12-entry tables, 60 item entries and 300 object pointer/distance pairs. | **Confirmed missing, migrated and storage-tested.** A 128-active Balloon Battle startup corrupts the caller stack with actor/distance pairs in USA `Audio::RaceMgr::SortAndToggleActors` (`8070B0B4`), downstream of the already expanded ObjectsMgr pools. | Expand the stack layout and every base/end/save/restore/iteration operand while retaining original collection, classification, ordering calls and sound admission limits. | Original translated loops, guarded full object and racer/item fields, strict lifetime watches, all boundary counts, unchanged twelve-capacity emission and rebuilt live Balloon Battle. |
| ObjectsMgr fixed object-registration pools and interaction vectors. | **Already covered for tested manager paths through 128.** Existing `ObjectPoolCapacity` derives `80 + 10*N` entries rather than retaining the original 200-object budget. | Preserve the checked allocations/counts. Qualify rendered object resources and full battle lifecycle separately. | Original USA manager allocation, vector initialization, complete pool registration and destruction pass all thirteen required capacities, including 1,360 entries at 128. Resource constructors remain callbacks. |
| AI manager controller arrays, rank/route subobjects and battle team lists. | **Partially covered.** `AIManager`, `AIVsSub`, `AIEnemyRoutes`, `AIRacerObjects` and battle roster layouts already expand these families. Strict-audited original AI manager initialization, both controller lookups and route initialization pass all thirteen required capacities; automatic manager constructor/destructor/release watches now join the ArchiveMgr watches. | Map each vanilla reference site to our corresponding function and check indirect, pointer-walk, reset and destructor paths; do not infer a gap from unchanged index strides alone. | Existing native fixtures extended to boundaries, plus retired-access traces and high-ID AI movement/respawns. |
| Per-controller battle opponent selector: two six-entry pointer/state arrays and following scalar, `0x40` allocation. | **Confirmed missing, migrated and fixture-verified.** A real 128-active Coin Runners update reached gameplay, then the population loop overwrote neighboring AI selectors with opponent pointers. The parent inlines allocation, construction, reset, population and several lookups; protecting only standalone methods is insufficient. | Expand both arrays to the existing team-roster capacity `ceil(N/2)`, move the tail, inventory inline consumers, and retain full target eligibility for original six-way queries. | Native guarded original-code fixtures, strict stale-read/write controls, odd team sizes, high-ID targets, two lifetimes and a rebuilt full battle. |
| Input virtual-controller holders, stride `0x180`. | **Partially covered.** `InputManager` already models per-racer holders and relocated following controllers; native boot/layout fixtures exercise this. | Check construction, pointer differences, attachment and teardown at 128, preserving four physical screen/controller roles. | High-ID CPU input and guarded lifecycle fixtures, with no physical-controller expansion. |
| Item players/online partner pointers and retained object-holder tail. | **Partially covered.** `ItemManager` and nested item state have relocated per-racer arrays and following fields. Automatic constructor/destructor/release watches now cover the manager before singleton publication. Strict-audited original initialization, partner-cache indexing, all fifteen object holders, updates, reset and destruction pass all thirteen required capacities over two lifetimes; resource constructors remain callbacks. | Audit the vanilla reference sites and computed/indirect readers; keep network-only work parked. | High-ID vanilla item use, reset/destruction and native/guest access agreement at 128. |
| Gesso/Blooper state, POW affected flags and Race HUD balloon positions. | **Partially covered.** `GessoManager`, `PowManager` and `RaceBalloons` distinguish participant arrays from four screen slots/severity roles. Original POW/Blooper construction, per-racer targeting, independent one-to-four screen slots, updates and cleanup fixtures now pass all thirteen required capacities, including the highest racer IDs at 127/128. Race HUD balloon presentation remains a separate qualification. | Extend active high-ID effects and all computed readers, rather than growing fixed screen/severity arrays. | Racer 127 targeting/effects, screen bounds and no unexplained retired-layout reports. |
| Driver models, vectors, targeting, counters, rankings, finish records and tail. | **Partially covered.** `DriverManager` explicitly models these arrays, race positions and following scalars. Strict-audited original lifecycle/finish-classifier fixtures now pass all thirteen required capacities; constructor/destructor/global-delete watches add live coverage. | Compare every vanilla access-map site, including pointer subtraction and bulk copies; validate mixed resources and teardown. | High-ID model/item/finish accesses, mixed roster race and second race with lifetime-aware instrumentation. |
| Start and respawn tables indexed beyond twelve. | **Partially covered, using our own approach.** Checked grid tables and `StartingGrid::Pack` keep expanded starts within the authored footprint; the JGPT wrapper packs actual original rescue samples. MKW100 instead repeats course-aware blocks and reuses respawn slots. | Keep our packing architecture; validate real course geometry/checkpoints and all 128 positions. Existing private placeholder-coordinate probes are not course validation. | All vanilla courses/arenas, collision/checkpoint checks, racer 127, forced falls and resumed movement. |
| Signed-byte counts/ranks and three-digit positions. | **Confirmed position-tracking hazard fixed; other consumers remain partially verified.** USA `8052EC08` sign-extended one-based position 128 to -128 before scratch indexing. Format-32 `rank8` preserves unsigned ranks and the `0xFF` none sentinel at this checked consumer. IDs 0..127 still fit a signed byte; physical HUD-slot `-1` remains a real sentinel. | Use the reference signed-byte site list as a typed discovery map. Widen only proven racer/count/rank semantics; retain unrelated sentinels. | 127/128 counts and ranks, invalid/none sentinels, localized original-style three-digit indicators. |
| Results: every participant visible together. | **Partially covered after compact asset migration.** The generator now supplies 16 rows × 8 total columns at 128 for individual and team results, with retail fonts, portraits, team colors and three-digit rank artwork. Smaller capacities optimize card scale within the authored window. Twelve/24 layouts remain unchanged. | Qualify the compact cards in the actual results flow. Geometry and original-reader checks complement rendered readability; no pagination, scrolling, tabs or rotating subsets. | Team result allocation, control construction, unused-row clearing and destruction pass all thirteen required capacities. Still render all 128 entries together in 16:9 and 4:3, including individual/GP/team/battle results and menu return. |

"Already covered" describes the named protection, not every runtime consumer or
complete gameplay support. "Partially covered" means concrete protection exists
but its consumer/lifetime/boundary qualification is incomplete. An unexplained
instrumentation hit is a lead until its intended field and owner are established.

## Next implementation: retired-layout access auditor

Generate old/new type and field ranges, counts, strides, sizes and applicable
relocation sites from the existing metadata. Register live allocation and stack
instances with generations/lifetimes. Observe reads and writes in translated and
native/HLE guest-memory paths, retaining guest-site provenance when available.

An old offset may now be a valid element of another live field. Address overlap
alone must therefore not trigger a strict failure. Distinguish proven stale
field-intent accesses from ambiguous overlap leads and legitimate current-layout
accesses. Track object retirement/reuse, aggregate hits by site/access/field, and
export JSON. Provide log and strict modes. Leave memory unmodified unless a
separate, proven dead non-overlapping region explicitly permits a canary.

First demonstrate a deliberately stale translated access, including an overlap
with another live field, correct access, native/HLE access and object reuse.
Then use the facility on actual newly inventoried consumers. Keep independent
counter-width, resource-count and arithmetic audits: memory monitoring cannot
prove those safe.

Minimum capacity set: **12, 13, 24, 25, 26, 48, 52, 64, 100, 116, 118, 127, 128**.
The baseline remains the original twelve-player path. Gameplay qualifications
cover VS, GP scoring, team scoring, both battle modes, effects/items, mixed
rosters, starts/rescues, all-results rendering, teardown and subsequent startup.

## Instrumented VS checkpoint, 2026-11-15

[The machine-readable record](validation/128-capacity-vs-instrumented-13-24.json)
identifies the exact source/binary, normal clock, native completion flags and
auditor coverage for the first 13- and 24-active-racer probes at build capacity
128. Both reach stage four with every active racer observed and flagged finished.
No reported hits or skipped stack-lifetime entries occur in these exercised
watches. The 13-racer process then receives an intentional diagnostic SIGABRT:
complete MEM1/MEM2/overlay snapshots and normal fatal-handler exit verify that
crash-dump reads no longer re-enter the auditor. This is not a natural crash.
The 24-racer process is stopped by its driver after completion.

These headless null-renderer probes cover the persistent ArchiveMgr and generated
stack watches. They do not qualify other heap-family lifetimes, every AI's path,
all effects, rendered results, complete teardown, or another race. The original
12/13/24/48/100 team-result storage fixtures are retained and extended through all
required boundaries; all thirteen cases pass, including both team control arrays
at 128. That storage proof remains separate from visual qualification.

## Read-only per-racer live observer

`runtime/tests/offline_capacity.py` supplements the aggregate progress log with
verified USA Kart::Link positions, unique per-slot physics pointers, lap counters,
player update frames and sampled respawn flags. It uses the existing test-only
RAM backing helper and opens those files read-only. It never injects controller
input, changes guest RAM or scales the clock. For example, from a Linux portable
product with the auditor compiled in:

```sh
DISPLAY=:99 SDL_AUDIODRIVER=dummy python3 runtime/tests/offline_capacity.py \
  --product /path/to/product --source BUILT_REVISION \
  --players 26 --capacity 128 --mode balloon --timeout 2400 \
  --log /tmp/balloon26.log --report /tmp/balloon26.json \
  --audit-report /tmp/balloon26-audit.json
```

The report records the executable hash, arguments, per-racer snapshots, sampled
distance and stationary intervals. `native-finish-flags` means exactly that; it
does not claim that every CPU completed the course. Missing sampled movement or
lap advance is listed separately for investigation, as are slots not observed
entering the lap after the configured final lap. Entering lap one from the grid
alone is not a course completion. Snapshots are sequential,
not atomic; sampling can miss short rescues, and stationary intervals after an
individual racer finishes are possible. Battle modes have no lap requirement.
Retain the separate forced-fall probe for proving rescue and resumed movement.

The auditor also observes the raw 32-byte `dcbz` helper, aligned `DCZeroRange`
writes and both sides of locked-cache load/store/data copies before mutation.
The strict `dcbz` check runs outside its scalar fallback catch so it cannot be
swallowed and retried. Controlled native fixtures prove stale reads and writes
stop before copying/zeroing, while correct relocated accesses retain overlapping
`memmove` behavior and GX DMA notifications. Enabled and disabled fixtures pass.
Known field intent also identifies a stale starting pointer when the transfer
extends beyond the old field into neighboring storage. A controlled 96-byte
copy starting in a retired 64-byte tail is rejected before mutation, with its
full width reported. Unattributed ranges retain conservative classification.
This closes those specific raw-pointer escapes; it does not instrument every
host pointer user or prove coverage for bulk ranges starting outside a field.

Watched constructors publish JSON checkpoints immediately before guest
construction and after success. This retains an early lifetime snapshot when
resource loading fails before the first race-progress log. Fatal memory dumps
still disable observation to avoid re-entering a mutex interrupted by a signal;
an early checkpoint is last-known state, not a claim to contain every final
access before the failure. Enabled/disabled lifetime fixtures verify that the
live-object report exists before the original constructor runs.

## Live balloon counter boundary, 2026-11-15

[The 26-active-racer record](validation/128-capacity-balloon-observed-26.json)
covers a full 180-second Block Plaza Balloon Battle timer in the 128-capacity
executable at normal clock timing. All 26 slots have distinct physics storage
and substantial sampled movement. The observer samples 74 respawning states;
these are observations, not a count of distinct rescue events. The native game
reaches stage four at frame 11031 with all 26 finish flags. The exercised
ArchiveMgr/stack watches report no hits across 31.2 billion observed accesses.

This crosses the original ten-per-player byte-count boundary with actual game
resources, complementing the widened-count fixtures. It does not establish
balloon visibility, score/ownership correctness, all heap lifetimes, teardown,
or a 128-active battle.

## First 128-active VS completion, 2026-11-15

[The exact-build record](validation/128-capacity-vs-observed-128.json) captures
a normal-clock, one-lap Luigi Circuit run with 128 active racers at capacity 128.
Every sampled racer has a distinct physics pointer, finite positions and more
than 136,000 units of sampled movement. Racer 127 moves and enters lap two.
None stays stationary across consecutive samples. At native completion, stage
four/frame 2472, 123 racers have been sampled entering lap two; five receive
automatic finish flags while still on their final lap. This is not evidence
that all 128 independently crossed the finish line.

The exercised ArchiveMgr, AIManager and generated stack watches report no hits
or skipped stack-lifetime entries across 26.5 billion observed accesses. Other
heap families and the later bulk-cache instrumentation are not covered by this
binary. Full-course/mode coverage, forced rescues, scoring, rendered results,
teardown and another race remain open, as does future 128-console networking.

## Forced rescue at all 128 racer IDs, 2026-11-15

[The controlled fall record](validation/forced-falls-rmce01-128.json) uses a
128-capacity executable with 128 active racers on Luigi Circuit. At race frame
600 the probe lowers only each kart's current/previous Y positions. It does not
set rescue flags or lap state. All 128 slots enter native respawning states at
frames 729–730, return above the fall threshold at frames 867–868, and resume
measurable movement at frames 987–996. By frame 3137 every slot has also advanced
a lap after the fall, including racer 127. The driver then stops its owned game.

Auditing is off for this independent functional probe. This validates the
exercised all-slot rescue/recovery path on one course; it does not qualify all
courses, battle respawns, scene ownership or 128-console networking.


## Battle sound-heap pressure discovered at 128

A real 128-active Coin Runners probe built from `3173994` failed during race
resource construction, before the first gameplay progress sample. Its 54.3 MiB
MEM2 structure heap reported only 32,796 free bytes when a 90,680-byte model
allocation failed. The subsequent invalid model access is a consequence requiring
retest, not evidence of a Coin Runners stack-displacement failure.

The retained MEM1 structure owner had 7,354,476 free bytes in the fatal snapshot.
A 7,340,288-byte child allocation occupied MEM2. Original RMCE01 code identifies
it as the **battle sound heap**: `RaceScene::CreateAndInitInstances` (`8054E1E8`)
selects `Audio::GameHeap` type 2; `SetType` (`806F911C`) returns the original
7 MiB table size plus `0x100`. The factory call at `8054E2B8` reads the secondary
slot `+0xC98` and retains the child at scene `+0xC70`. `GameScene::Reset` destroys
that child at `80516CF4..80516D14`. VS uses a different 10 MiB sound budget.

`ExpHeap_CreateBattleSoundHeap` is restricted to the verified USA caller, exact
battle request and expanded builds. It tries the retained primary backing owner
at `+0xC88`; if original creation returns null it restores the call context and
retries the original parent. The original factory records the actual parent at
child `+0x18`; original destruction frees to that owner. Existing default routing,
backing owners, sound sizes and root partitioning remain intact. This avoids an
individual-allocation ownership map or a new overflow arena.

Both retail-capacity and expanded native fixtures pass. They pin the USA caller
instructions and sound-size table, execute the original child factory, parent
lookup and destroy routines, and check selected ownership, failed allocation,
failed construction cleanup, original-parent retry, unrelated callers/requests,
missing/aliased owners, preserved registers and two successive lifetimes.
Parent allocation and low-level heap construction/destruction are fixture
callbacks. A clean rebuilt live Coin Runners run, real sound resources and scene
round trips remain necessary before closing this resource-budget issue.


The first clean sound-parent build (`2851cbc`, SHA-256
`3fd3f498107a7450c0bde549dfab468dde3cfd54fd4e1829393de2dc5c53779a`)
confirmed the child actually belongs to MEM1. Course `0x21` still exhausted MEM2
later during model loading, with only 65,128 bytes free for a 117,368-byte request;
its untouched debug owner still had 5,830,048 bytes free. This remains a separate
resource-budget lead. The original Block Plaza case (`0x20`) passed loading and
reached the first battle update, exposing the missing battle-ranking stack frame
above. Neither run qualifies completed 128-active Coin Runners gameplay. Both
early audit reports contain no hits in their modeled lifetimes; the missing
ranking frame was absent from that metadata and could not be watched yet.


## Clean 128-participant VS results in 16:9

A clean `2851cbc` build with the checked main ranking fix completes the one-lap
128-active Luigi Circuit VS probe and produces a rank-to-participant permutation
containing every ID `0..127` exactly once. Every inverse position matches. The
rendered results show all 128 cards in 16 rows and eight columns, with one local
highlight and the retained retail fonts/materials/rank glyphs. Final point totals
are visible after the original score-count animation completes.

[The rendering and read-only ranking record](validation/128-capacity-vs-rendered-128-16x9.json)
contains the binary hash, every ranking/inverse-position value and explicit pause,
termination and qualification limits. The earlier duplicate ID zero/missing ID 60
is absent. Four-to-three, GP/team/battle flows and second-race qualification of
this rebuilt source remain separate work.


## Reclaim the unused NDEV reserve within the existing root partition

The live 128-active battle probes established remaining MEM2 pressure even after
moving the sound child: course `0x21` failed a 117,368-byte model request, and
Block Plaza retained only 87,432 free bytes at the later battle-ranking fault.
Both snapshots showed the separate debug owner entirely free (5,830,048 bytes).
Original `GameScene::CreateHeap` (`80517470`) selects an allocation slot by its
heap index; its sole direct race caller (`8054E550`) requests secondary index 1.
Debug-slot direct references are primarily state flags and teardown. Indexed or
indirect debug consumers remain a live qualification concern.

Retain the checked root partition and distinct owners, but make the NDEV reserve
decay as `32 * floor(279871488 / (N*N))`. Capacity-aware axis/divisor metadata
supplies `N*N`; the original twelve-racer reserve remains exactly `0x3B50000`.
At 128 the reserve is 546,624 bytes, reclaiming 5,284,032 bytes for the existing
MEM2 owner without enlarging mapped memory or changing any allocation/free route.
The debug owner remains present. This replaces the earlier linear inverse reserve
curve; it does not import an overflow allocator or ownership map.

All thirteen original root-initializer fixtures pass for retail and NDEV memory
extents, with aligned non-overlapping partitions, original system-heap ownership,
register/structure guards and identical emitted twelve-capacity code. All 977
unit tests pass. Live resource loading, effects and scene round trips must still
verify the smaller debug budget and reclaimed MEM2 headroom before this pressure
issue is considered resolved.


The clean `2851cbc` binary also renders all 128 cards together in **4:3**, in a
960-by-720 viewport within the 1280-by-720 window. The first race and a second
race after the original results/next-race/scene teardown flow both retain every
participant ID exactly once and matching inverse positions. Final 16-by-8 results
were captured after the second race, with one local highlight and retail styling.
[The 4:3 record](validation/128-capacity-vs-rendered-128-4x3.json) preserves both
rank maps and capture limits. GP/team/battle results remain unqualified.


## Battle opponent-selector migration

The `7137b24` live 128-active Block Plaza Coin Runners probe passes initialization
and countdown, then fails at the first active AI update. `8072492C` calls a
neighboring selector whose vtable was overwritten by opponent pointers. The
responsible object at `93C4507C` had an original `0x40` allocation: header `+0`,
six pointers at `+0xC`, six state words at `+0x24`, and a trailing word at `+0x3C`.
`80724678` and `80725798` already populate it from the enlarged opposing roster.
This helper is distinct from the already migrated AIManager and team rosters.

`AIBattleTargetSelector` uses the same `ceil(N/2)` dimension as those rosters.
At 128 it has 64 pointers, 64 state words and size `0x210`; states start at
`0x10C` and the trailing scalar moves to `0x20C`. The migration pins the live
allocation immediate at `80723824`, inline constructor tail store at `807239A4`,
standalone construction/reset, both original reset fills, the update count and
state addresses, and all discovered inline state reads. Eight extra reads use an
advanced `r5` base rather than `r4`; they are inventoried separately. The superficially
similar `8072664C` load belongs to another selector and is not rewritten.

A generated companion export supplies native target-array offsets and capacity
without changing the established `MkwGuestLayout::Layout` ABI. The standalone
first-match wrapper scans the relocated arrays. For the original inlined six-way
queries, the update wrapper first runs the original distance/angle classifier
across all slots, then stably promotes the first non-null representative of each
of its five fixed state classes into the prefix. It retains every paired target
and state in the same guest arrays; no persistent shadow storage is introduced.
Each current classification's first matching identity is preserved. Expanded AI
retains this cache ordering between updates, so later priority can favor previous
representatives; the twelve-capacity path calls through without promotion.

The controlled original-code fixture supplies guarded expanded backing while
observing the original 64-byte request. Strict mode stops its stale inline tail
write before changing a live target slot, then stops the original stale state
read. Positive tests cover the allocation/inline-construction seam, standalone
constructor, real inline reset/population paths, full classification iteration,
advanced-base inline lookups, nulls, all state classes, high-ID targets, guards,
reset and destruction over two lifetimes. Position and angle helpers use fixture
callbacks; complete battle gameplay remains a separate qualification.


All fourteen final selector cases pass: the thirteen required capacities and the
original-code stale-access control at 128. All 977 unit tests and the complete
declared-consumer proof/original-emission/expanded-compilation check also pass.
The new native source compiles with instrumentation disabled. The live battle
retest must include the smaller NDEV reserve and the migrated selector together;
none of these fixture results qualifies a completed 128-active battle yet.


## First-use watches for inlined battle selectors

`EnsureInstance` atomically reuses an existing watch only when type, base,
allocation extent and owner agree. Ordinary `RegisterInstance` retains duplicate
rejection. Releasing and reusing storage creates a new generation. This lets a
native first-use hook enroll an object whose constructor was inlined without
silently replacing another lifetime or weakening size/owner checks.

The battle update wrapper enrolls the migrated selector before guest classification.
Standalone construction has its usual pre-call watch; destructor and global-delete
hooks retire deleting and non-deleting lifetimes. Inlined initialization/population
before the first update is still covered by static and controlled native fixtures,
not by this live first-use watch. No guest allocation or game state is changed.

Eight constructor/destructor configurations plus both auditor foundation cases
pass. They retain earlier overlap, stale read/write, reuse and cleanup tests and
add idempotence, mismatched extent/owner rejection and fresh generation checks.
The 128 selector fixture reuses its preexisting watch correctly; both C++17 header
configurations and all eight native import/index tests also pass. A rebuilt live
expanded battle must verify these added watches in the actual scene lifecycle.

## Expanded race-rank sound sequence variable

The reference signed-byte consumer list led to USA
`Audio::RaceRSARPlayer::PlaySound` at `8070EB64`. Its E5/E6 path reads the
one-based position at `RaceinfoPlayer+0x20`, sign-extends it at `8070ECA8`,
computes `12-position`, and calls `Audio::Manager::WriteSeqVariable` at
`8070ECB4`. Rank 128 therefore produced 140, outside the authored 0..11 axis.
This is distinct from safe zero-based racer IDs 0..127.

A narrow native wrapper for the existing sequence-variable writer recognizes
only that checked caller and variable zero. Expanded active rosters map their
valid ranks monotonically onto the original twelve-position axis. It recovers
the unsigned byte from the original producer's value; no game structure or
relocation system changes. Smaller active rosters, other callers/variables,
invalid ranks and the zero/FF sentinels retain original arguments and call-through.

Both 12- and 128-capacity native fixtures pass: every byte position, all thirteen
required active-count boundaries plus zero/one/two, caller/variable isolation,
missing RaceData, endpoint/order checks and register/stack preservation. Six
original USA producer words are pinned. The fixture substitutes the final
sequence-variable callee; audible mixing and DSP output remain unqualified.
This fix does not implement the separately queued multi-instance mixing goal.

## Auditor scaling with live per-racer watches

The first-use selector hooks increase the live watch set from three managers to
131 instances in the running 128-active Coin Runners test. The existing atomic
page filter avoids most accesses, but every positive page previously scanned
all instances and repeated an ownership scan for each relocated field. Every
translated function return also scanned all instances for a possible stack watch.

The diagnostic now indexes live generation IDs by guest page and stack frame.
Positive accesses inspect only candidate instances on intersected pages;
cross-page ranges deduplicate generations. Ownership is computed once per
candidate and only where field intent needs it. Return-time cleanup looks up its
own frame. Registration/retirement update both indexes under the existing lock;
no guest pages, allocation ownership or hit classifications change.

All four auditor/bulk foundation tests pass, retaining translated stale reads and
writes, strict-before-write behavior, current-field overlaps, generation reuse,
thread counters and suspended-stack retirement. Added cases keep 128 independent
watches live while checking cross-page accesses, ambiguous overlapping instances,
strict stale writes and final retirement. A private optimized native microbenchmark
of 200,000 attributed current-field reads across 128 independently paged objects
measured 0.577209 seconds before and 0.017504 seconds after page indexing. This is
a synthetic lookup measurement, not a gameplay speed claim; rebuilt live timing
and completed battle qualification remain open.

## Audio actor sorting stack: live Balloon Battle discovery

The clean `addc9dc` 128-active Balloon Battle probe on Block Plaza failed during
startup after 20.06 seconds, before its first race snapshot. Its existing watches
reported zero hits, but did not yet model this stack frame. The reported audio
virtual-call fault was downstream: the crash snapshot contains object actor
pointers paired with distance `27078.1875` beyond the 300-entry temporary table,
through the saved registers and caller frame. Original USA `80709408` calls
`Audio::RaceMgr::SortAndToggleActors` (`8070B0B4`) immediately before that fault.

The routine has independent stack tables: ten special actors, twelve kart
actors, twelve racer actors, sixty item actors and three hundred object actors.
Each record is an eight-byte pointer/distance pair. Expanding ObjectsMgr alone
does not protect these computed stack writes. `SoundActorSortFrame` expands
both racer tables to N, items to 5*N and objects to 180+10*N, retaining the original
300-object allowance at twelve and exceeding ObjectsMgr's 80+10*N total pool.
At 128 the object scratch holds 1,460 pairs and the frame grows from `0xC90` to
`0x4A30`. Fifty-four checked stack operands and four iteration bounds cover
initialization (including unrolled end pointers), collection, sorting arguments,
toggling, spills and restoration. Original admission limits and classification
remain unchanged. The original PSQ instructions' signed negative displacement
is retained; it is not mistaken for a positive `+0xC88` layout field.

All thirteen capacity fixtures and an original-code negative control pass with
the original collection/distance/toggle loops and strict stack watches. The
negative control stops the actual stale FPR save at `8070B0C0` before it writes
the expanded object region. Alternating full object and mixed racer/item fields
exercise four invocation lifetimes, guards and register restoration. Twelve-player
C++ output is byte-for-byte unchanged. All 977 unit tests and the complete
declared-consumer proof/original-output/expanded-compilation test pass. The
fixture now also executes the original guest heapsort and distance comparator;
all fourteen strengthened cases pass. Rebuilt 128-player Balloon Battle and Coin
Runners have reached active gameplay with no reported hits; completion remains
pending. List traversal, positions, item eligibility and toggle endpoints are
controlled callbacks, so this remains separate from live sound qualification.

## Driver and balloon manager lifetime coverage

These managers already have relocated storage; this change adds independent
runtime observation rather than classifying their layouts as missing.
DriverMgr's original factory calls USA constructor `80783A50` before publishing
its singleton, and its release path invokes destructor `80783E18`. Narrow wrappers
watch the current allocation before construction and retain the watch through
destruction. The balloon factory similarly calls `80865454`; its standalone
destructor is `80865568`. Ordinary scene cleanup can delete the balloon singleton
directly, which the existing global-delete retirement hook already covers.
PAL wrapper identities are mapped using the repository's region table and the
USA factory/callee instructions; no PAL patch addresses enter the manifest.

All twelve lifetime-wrapper configurations pass (six manager types, auditing on
and off), including constructor/read diagnostics, failed construction, destruction,
release-before-reuse and fresh lifetimes. All eight native import/index tests pass.
DriverManager's original initialization, model/position/timer walks, targeting,
rank/counter updates, four rank-based modes and three finish classifiers now run
with a strict manager watch at all thirteen capacities. The 24-racer fixture
initially exceeded its unchanged compiler timeout under CPU contention; its
isolated retry passes. Model resources remain callbacks. Rebuilt live coverage
of these two new manager watches and complete scene lifetimes remains open.

## Single-writer auditor counters with a shared overflow path

Observed/filtered counters previously used locked read-modify-write operations
for every guest access, including page-filter negatives. The first 64 host
threads now each own a never-reassigned counter slot. They update that slot with
atomic loads/stores, and reports still read it atomically. Additional threads use
a separate shared slot with atomic fetch-add; they cannot race an exclusive
writer. Reset clears both sets without reassigning existing thread bindings.

All four auditor/bulk foundation tests pass, including a new synchronized
100-thread run that preserves exactly 100,000 additional observed and filtered
accesses across exclusive-slot exhaustion. Existing stale read/write, overlap,
strict-before-mutation, stack retirement and reuse checks remain intact. A private
optimized native benchmark of twenty million unwatched reads measured 0.347477
seconds before and 0.150225 seconds after this change. These are synthetic counter
path timings, not a gameplay-speed or completion qualification.
