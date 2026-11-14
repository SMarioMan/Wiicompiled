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
| ArchiveMgr: two `ArchivesHolder[12]` arrays, offsets `0x8`/`0x158`, stride `0x1C`; overflow reaches the other array and content archives. | **Confirmed missing.** No matching layout/global or native protection was found. USA factory `8053A72C` still requests `0x61C`; constructor `8053A7B0` passes 12 to both holder constructors at `8053A7C8`/`8053A808`. | Add a typed manager with both capacity-sized arrays and its retained tail; relocate allocation, constructors, destructors, lookups, end pointers and holder-to-racer conversions. | Real USA constructor/destructor and indexed/inverse lookup fixtures, mixed rosters, race exit/re-entry, auditor coverage. Derive sizes: do not hardcode the estimated 128-player offsets. |
| Balloon manager: `10*N` entries, `N` held-balloon records and change flags overwrite adjacent state. | **Already covered for storage.** `BattleBalloonManager` models the pool and trailing fields; `BattleBalloonStorageTests` executes real construction/reset/destruction. Active-count coverage stops at 24. | Retain this relocated pool. Extend runtime-count and object-ordinal coverage independently. | Full active fields at 25/26/48/52/64/128, allocation guards, ownership and lifecycle. |
| Balloon counts: byte fields `N`, `5*N`, `10*N`; pool totals wrap at 26/52 racers. | **Confirmed missing width migration.** The current header remains four opaque bytes; existing documentation explicitly excludes more than 25 active players. Reference `balloon_battle.cpp` also identifies balloon-object index dependencies. | Inventory every count load/store, truncated loop index and balloon-object ordinal before choosing verified widening or narrow native counters. Do not replace the relocated pool with shadow storage. | 1,280 distinct objects at 128; assignment, loss, stealing, reset, destruction and second race; no wrapped/infinite loops. |
| Effect creator namespace and EGG group-slot table. | **Partially covered.** Existing count/role relocations request `N+4` creators and test the real EGG constructor/table. USA `8022464C` forwards that count; the chosen reserved-role model differs from MKW100's `N+2` racer-group minimum. | Verify at least 132 creators and 130 usable racer EGG groups at 128; retain larger reserved-role capacity where required by our model. Audit lookup/destruction and boot-to-race lifetime. | Original table allocation/initialization and high-ID effects at 127, including reserved roles and second race. |
| NW4R activity lists: four per EGG group, record stride `0x1C`. | **Partially covered.** USA `802246C8` derives activity bytes from the EGG count (`80224748`), but `EffectCreatorStorageTests` mocks that entire memory-manager routine. This is a validation gap, not proof of a missing allocation change. | Execute the actual backing allocation and list constructors; verify count propagation, iteration and destruction. Our `N+4` group model may require 528 rather than the reference minimum of 520 lists. | Correct count, byte extent, list link offsets, guards and effects for racer 127. |
| Light-set pool: two requests per kart plus items; signed-byte light IDs cap safe expansion. | **Not yet verified.** No equivalent exhaustion/sharing protection has been identified, but absence of a similarly named wrapper is insufficient evidence. | Map PAL acquire/release/loader functions to USA, test exhausted allocation and all dereferencing callers, then implement ownership-safe sharing or another demonstrated solution. | 64/100/128 mixed rosters, item allocations, shared release, teardown and fresh pool on second race. |
| Coin Runners nested update scratch and signed-16-bit displacement threshold. | **Not yet verified at the boundary.** `BattleCoinUpdateFrame` already grows its nested arrays, save areas and complete frame. Existing native fixtures reach 100, not 116/118/127/128. | Test the current lowering first. Change lowering only if a full-width IR displacement or stack adjustment is truncated. | Actual translated prologue/epilogue, all spills/local-array accesses, stack guards and saved registers at 116/118/127/128. |
| Race-scene heap budgets and cross-heap ownership. | **Partially covered.** Checked `HeapRoutes` retain separate backing owners; the NDEV reserve shrinks with capacity. Real 24-player scene round trips and budget fixtures through 100 exist. This differs intentionally from MKW100's overflow arena. | Preserve routing/reserves. Measure mixed-resource peaks at 128 and verify every allocation/free/resize/reset owner before adding any fallback. | Effect/item-heavy races, mixed characters/vehicles, scene exit, second race and owner/heap guard reports. |
| AI manager controller arrays, rank/route subobjects and battle team lists. | **Partially covered.** `AIManager`, `AIVsSub`, `AIEnemyRoutes`, `AIRacerObjects` and battle roster layouts already expand these families. | Map each vanilla reference site to our corresponding function and check indirect, pointer-walk, reset and destructor paths; do not infer a gap from unchanged index strides alone. | Existing native fixtures extended to boundaries, plus retired-access traces and high-ID AI movement/respawns. |
| Input virtual-controller holders, stride `0x180`. | **Partially covered.** `InputManager` already models per-racer holders and relocated following controllers; native boot/layout fixtures exercise this. | Check construction, pointer differences, attachment and teardown at 128, preserving four physical screen/controller roles. | High-ID CPU input and guarded lifecycle fixtures, with no physical-controller expansion. |
| Item players/online partner pointers and retained object-holder tail. | **Partially covered.** `ItemManager` and nested item state have relocated per-racer arrays and following fields. | Audit the vanilla reference sites and computed/indirect readers; keep network-only work parked. | High-ID vanilla item use, reset/destruction and native/guest access agreement at 128. |
| Gesso/Blooper state, POW affected flags and Race HUD balloon positions. | **Partially covered.** `GessoManager`, `PowManager` and `RaceBalloons` distinguish participant arrays from four screen slots/severity roles. Existing original-function fixtures cover expansion. | Extend active high-ID effects and all computed readers, rather than growing fixed screen/severity arrays. | Racer 127 targeting/effects, screen bounds and no unexplained retired-layout reports. |
| Driver models, vectors, targeting, counters, rankings, finish records and tail. | **Partially covered.** `DriverManager` explicitly models these arrays, race positions and following scalars. | Compare every vanilla access-map site, including pointer subtraction and bulk copies; validate mixed resources and teardown. | High-ID model/item/finish accesses, mixed roster race and second race with lifetime-aware instrumentation. |
| Start and respawn tables indexed beyond twelve. | **Partially covered, using our own approach.** Checked grid tables and `StartingGrid::Pack` keep expanded starts within the authored footprint; the JGPT wrapper packs actual original rescue samples. MKW100 instead repeats course-aware blocks and reuses respawn slots. | Keep our packing architecture; validate real course geometry/checkpoints and all 128 positions. Existing private placeholder-coordinate probes are not course validation. | All vanilla courses/arenas, collision/checkpoint checks, racer 127, forced falls and resumed movement. |
| Signed-byte counts/ranks and three-digit positions. | **Not yet verified at 127/128.** IDs 0..127 fit a signed byte, but count/rank 128 does not; physical HUD-slot `-1` remains a real sentinel. | Use the reference signed-byte site list as a typed discovery map. Widen only proven racer/count/rank semantics; retain unrelated sentinels. | 127/128 counts and ranks, invalid/none sentinels, localized original-style three-digit indicators. |
| Results: every participant visible together. | **Confirmed missing 128 presentation.** The expanded-UI generator explicitly caps results at 100; existing 24-player layouts are not a compact 128-entry design. | Create a genuine dense single-screen grid with readable ranks and participant identity, retaining scores/times/team information as space permits. No pagination, scrolling, tabs or rotating subsets. | Render all 128 entries together in 16:9 and 4:3, individual/GP/team/battle results and menu return. |

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
