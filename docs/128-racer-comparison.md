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
| ObjectsMgr fixed object-registration pools and interaction vectors. | **Already covered for tested manager paths through 128.** Existing `ObjectPoolCapacity` derives `80 + 10*N` entries rather than retaining the original 200-object budget. | Preserve the checked allocations/counts. Qualify rendered object resources and full battle lifecycle separately. | Original USA manager allocation, vector initialization, complete pool registration and destruction pass all thirteen required capacities, including 1,360 entries at 128. Resource constructors remain callbacks. |
| AI manager controller arrays, rank/route subobjects and battle team lists. | **Partially covered.** `AIManager`, `AIVsSub`, `AIEnemyRoutes`, `AIRacerObjects` and battle roster layouts already expand these families. Strict-audited original AI manager initialization, both controller lookups and route initialization pass all thirteen required capacities; automatic manager constructor/destructor/release watches now join the ArchiveMgr watches. | Map each vanilla reference site to our corresponding function and check indirect, pointer-walk, reset and destructor paths; do not infer a gap from unchanged index strides alone. | Existing native fixtures extended to boundaries, plus retired-access traces and high-ID AI movement/respawns. |
| Input virtual-controller holders, stride `0x180`. | **Partially covered.** `InputManager` already models per-racer holders and relocated following controllers; native boot/layout fixtures exercise this. | Check construction, pointer differences, attachment and teardown at 128, preserving four physical screen/controller roles. | High-ID CPU input and guarded lifecycle fixtures, with no physical-controller expansion. |
| Item players/online partner pointers and retained object-holder tail. | **Partially covered.** `ItemManager` and nested item state have relocated per-racer arrays and following fields. Automatic constructor/destructor/release watches now cover the manager before singleton publication. Strict-audited original initialization, partner-cache indexing, all fifteen object holders, updates, reset and destruction pass all thirteen required capacities over two lifetimes; resource constructors remain callbacks. | Audit the vanilla reference sites and computed/indirect readers; keep network-only work parked. | High-ID vanilla item use, reset/destruction and native/guest access agreement at 128. |
| Gesso/Blooper state, POW affected flags and Race HUD balloon positions. | **Partially covered.** `GessoManager`, `PowManager` and `RaceBalloons` distinguish participant arrays from four screen slots/severity roles. Original POW/Blooper construction, per-racer targeting, independent one-to-four screen slots, updates and cleanup fixtures now pass all thirteen required capacities, including the highest racer IDs at 127/128. Race HUD balloon presentation remains a separate qualification. | Extend active high-ID effects and all computed readers, rather than growing fixed screen/severity arrays. | Racer 127 targeting/effects, screen bounds and no unexplained retired-layout reports. |
| Driver models, vectors, targeting, counters, rankings, finish records and tail. | **Partially covered.** `DriverManager` explicitly models these arrays, race positions and following scalars. | Compare every vanilla access-map site, including pointer subtraction and bulk copies; validate mixed resources and teardown. | High-ID model/item/finish accesses, mixed roster race and second race with lifetime-aware instrumentation. |
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
