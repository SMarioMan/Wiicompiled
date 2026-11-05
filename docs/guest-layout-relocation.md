# Guest layouts and code growth

Generated native functions use C++ labels and function symbols. The C++ compiler
resolves their actual jump distances after code generation. Wii PCs remain stable
identities for callbacks, LR continuations and dispatch; they are not offsets in
the native executable. Adding IR or native C++ does not require moving those Wii
identities or manually fixing native branch displacements.

Changing an inline guest array requires separate layout relocations. For example,
the original USA race-data constructor allocates `0x73f0` bytes. A contiguous
24-player array in each of its three scenarios requires `0x95b0` bytes. That size
does not fit the original PowerPC `li` instruction's signed immediate. Rewriting
the lifted operation before SSA allows the native compiler to materialize the
full value, without inserting bytes into the original PowerPC instruction stream.

`GuestLayouts` calculates guest field offsets, array counts, strides and structure
sizes from explicit PowerPC alignment and size facts. Host pointer sizes and host
`sizeof` are not used. Increasing capacity moves the fields following each array
and changes enclosing structure sizes. It creates one expanded array, rather
than keeping twelve slots and allocating a second set beside them.

`GuestLayoutRelocations` binds those symbols to audited instruction operands.
Every reference specifies its original instruction word, lifted node and operand.
Both the word and the original operand value must match before substitution.
Relocations run before SSA and inlining, including speculative callee decodes.
Loads, stores, allocation constants, address arithmetic and comparison bounds are
supported. `argument:N` also relocates a lifted helper argument; address additions
that set carry relocate both their arithmetic operand and their carry helper.
The pass does not guess which occurrences of the number twelve are
player counts.

## Current migration status

The initial USA manifest at
[`racedata.json`](../projects/mkwii-ntsc-u/layouts/racedata.json) describes the race,
menu and awards scenarios and audits 2,703 references across creation, construction,
destruction, scenario assignment, selected mode/menu readers, capacity scans and
point updates, plus AI manager and route-holder storage.
The manifest records 725 consumer entry points in `AuditedConsumers`.
Their declared receiver or singleton-pointer provenance is reproduced by the
integration suite. Every discovered field move must match its declared original
and expanded values. Their twelve-player C++ remains identical to the original (the verified copy
routine instead has full memory/register equivalence coverage),
and all expanded consumer bodies compile in native aggregate translation units.
This covers field references in menus, race logic, items and other systems;
it does not imply those systems' separate player arrays or limits are migrated.
It also binds the 54 original instruction words of the race-to-race bookkeeping
copy to a capacity-dependent array operation. Every original load and store must
prove the declared ordered field copies; branches, calls, extra memory accesses,
overlapping fields, partial spans and interior branch targets are rejected.
The replacement emits one copy per player before SSA while retaining the
original scratch-register outputs. This handles fully unrolled code that has no
loop-bound constant to relocate. `ArrayCopies` declares the original words,
base register, array/stride/count symbols and source/destination field widths.
These rewrites require manifest version 2, so older translators reject them
instead of silently ignoring required code changes. Version 1 reference-only
manifests remain supported.
The point updater's temporary score array grows with capacity. Its stack frame,
conversion scratch space, saved registers and caller LR slot move together;
increasing the loop alone would overwrite saved registers. The point updater's
Grand Prix/VS lookup now uses an expandable rank policy, described below, after
checking all 144 original table bytes and the original lookup instruction.
It is **incomplete**: other race-data consumers, race state, karts, items, scoring,
menus, starting grids and the LAN/game network layouts still require migration.
It is not a playable 24-player profile. Existing release builds retain twelve
players. The CLI rejects expanded builds from an incomplete manifest unless the explicit
development-only opt-in below is supplied.

Inspect a plan against the verified USA game inputs:

```sh
dotnet run --project translator/src/Translator.Cli -c Release -- \
  plan-guest-layouts --project projects/mkwii-ntsc-u/recomp.yml \
  --guest-layouts projects/mkwii-ntsc-u/layouts/racedata.json \
  --layout-players 24 --out build/layouts/racedata-24.json
```

The output records the computed layouts and each checked before/after operand.
Symbols include `Type.size`, `Type.field.offset`, `.size`, `.count`, `.stride`
and `.end`; `+` composes nested offsets. A reference may add a fixed field offset
with `Addend`, after multiplying the symbol by `Scale` (default one). Negative
scales express stack-frame allocation without hardcoded negative sizes.
The original player count is declared in the manifest. `Complete`
must remain false until every affected consumer and subsystem has been migrated
and gameplay has been tested. It is not a switch that implements missing work.

Exact division by a positive integer, such as `players/4`, rejects nonintegral
results. `ceil(players/4)` and `ceil(players/6)` cover a final partial group in
the original unrolled scans and initializers. Format version 4 adds 116 audited
`BoundedAccesses`: loads outside capacity return a declared inactive value and
stores outside capacity do nothing. Bounds use a declared logical index, or an
element-pointer difference and symbolic stride. Field relocation happens before
the bound is applied. Generated checks are omitted when capacity is an exact
multiple of the group width, including the 24-player target.

This grows default player initialization, menu/award resets and parameter
registration with capacity. The capacity scan preserves the separate physical
four-screen limit and original competition-mode participant limits. Race-info's
rotation setup also receives the configured capacity; its separate mask/table
representation still needs migration for capacities beyond 32.

## AI storage and verified fills

The AI manager has uniform controller and real-player-controller arrays. Its
allocation and six following pointer fields move with capacity. The route
holder's controller array, allocation and following count also move together.
Their original layouts and stores are checked against the USA binary. The
manager's cursor-based real-controller search is separately relocated and its
bound uses the configured capacity. The subordinate GP/VS AI object's CPU records also grow uniformly; its separate
four local-screen records and following state move together. The constructor's
full-chunk bounds use array ends rather than fixed original element offsets.
Its private and virtual consumers and three cursor-based local-array accesses
are included. Other AI data, including rank sorting
scratch arrays and battle-specific structures, still require migration.

Format version 5 adds `ArrayFills` for constructors that initialize every slot
with unrolled scalar stores. Each span declares exact instruction words, base
and value registers, and dense array offset/stride/count/width symbols. The
validator proves the complete ordered store sequence and rejects other effects,
overlapping arrays or rewrites, shrinkage, partial spans and interior branch
targets. Expansion emits stores for the full arrays before SSA; original-capacity
emission is unchanged. This avoids relying on allocator contents for new slots.

Native integration checks execute the real route-holder constructor, the real AI
manager initialization region before subordinate allocation, and both original
controller accessors, including the parameter-dependent mode selector, checking every slot and neighboring memory at 12, 13, 24,
48 and 100 slots. This verifies storage, not complete AI behavior or gameplay at
those capacities.

A separate native test executes the subordinate object's real initialization
region and insertion routine at those same capacities, using external callback
fixtures for CPU classification and action lookup. It fills every CPU record and
all four local records, checks counters, callee-saved registers, stack and heap
boundaries, and leaves neighboring state intact. This stresses storage roles;
it does not authorize a race with more than the configured participant count.

Declared `PointerTo` fields let the audit follow four-byte guest pointers to
their pointee types, including within indexed arrays. Missing target types,
non-pointer widths, interior unaligned loads and undeclared pointees are rejected
or left unresolved. These are explicit ABI declarations, never inferred from
host pointer sizes.

## Authored AI action profiles

The AI action allocation is one capacity-sized array of eight-byte records with
the original 16-byte SDK array cookie. Both allocation size and cookie count
scale together, as does the original action-loader loop. The independent
three-float speed allocation remains twelve **bytes**, and the source file keeps
its three difficulty rows of twelve authored profiles.

Format version 6 adds `ResampledCursors`. At a checked scalar cursor increment,
`NextIndex` declares the already incremented destination byte index. The source
cursor advances by the difference between successive closest authored ranks at
normalized destination positions: `round(i * (sourceCount-1)/(count-1))`.
This fills the uniform destination array without reading beyond an authored row
or adding extra slot banks. The original float-to-byte conversions execute
unchanged. Original-capacity code is byte-identical. Source/destination strides
must stay positive and unchanged; stale words, extra instruction effects,
shrinking dimensions and overlapping rewrites are rejected. Generated address
scratch variables stay local to the lifted instruction, including inside loops.

Native tests execute the original AI parameter constructor, actual SDK cookie
routine and all three difficulty loaders at 12, 13, 24, 48 and 100 slots. The
fixtures replace allocation and file I/O only. Every loaded byte is compared
with independently normalized authored profiles, including fractional input
conversion, and source data, stack, saved registers and allocation guards are
checked. This verifies parameter storage/loading; AI sorting scratch arrays and
complete racing still require migration.

## Virtual controller storage

`InputManager.virtualHolders` is one capacity-sized array of real 0x180-byte
virtual-controller holders. The manager allocation, SDK construction/destruction
counts, and fields after that array move together. Physical real controllers,
Wii/GCN/ghost devices and pad-status arrays retain their four local channels.
Fixed physical arrays with unchanged element strides can be indexed while their
base offsets move after the virtual array.

Native tests execute the original manager construction region, SDK array loop,
and complete virtual-holder constructor chain at 12, 13, 24, 48 and 100 slots.
They load the actual guest vtables, check each holder's controller bindings and
verify that neighboring fields, registers and stack/heap guards remain intact.
The default harness reports unexpected guest targets and MMIO before aborting;
missing dependencies still fail immediately. Other controller consumers and the
LAN module's explicit controller hook remain under migration.

## AI object ownership

The AI per-racer object owner has one pointer per configured participant after
its vtable. Its allocation, verified unrolled initialization, initialization loop
and destruction loop all use the same capacity. This corrects an observed heap
overwrite: pointers for later racers previously replaced the neighboring Lakitu
manager's fields. Lakitu retains its four local-player entries.

Native tests run the original owner constructor, insertion, initializer, lookup,
leaf destructor and owner destructor at 12, 13, 24, 48 and 100 slots. External
allocation/free and kart lookup are fixtures; every object and lifecycle step is
checked, with saved-register, stack and neighboring-allocation guards.

## Private executable validation

Incomplete manifests reject expanded production translation. For development
only, add `--experimental-guest-layouts` to a translation command with explicit
`--guest-layouts` and `--layout-players`. This allows building an executable to
find remaining migration problems; it does not mark the profile complete.

A successful translation emits `guest_layout_constants.h` and
`guest_layout_context.json` beside its function output. The constants describe
one uniform player array and the resolved offsets, sizes, counts and strides.
Base metadata version 3 records capacity, completion status, experimental status,
and SHA-256 digests of the exact manifest snapshot and generated header.
`emit-build-shards` verifies the header before publishing the build graph and
records its path and capacity in CMake. Mod translation rejects a base with a
different layout identity. Version 2 base metadata remains readable as the legacy
12-player ABI, without an explicit layout.

For example, a private full USA translation of 29,456 functions at capacity 24
completed with no unsupported instructions or invalid SSA functions and produced
72 shared native build shards. A private Linux executable also built in the
Linux Docker toolchain; whole-game race validation remains incomplete.
`--race-cpu` reports `[race-progress]` from the original simulation update:
stage, simulation frames, active/capacity counts, observed player records,
minimum per-player update frames, course and mode. The native wrapper calls
through to the winning original/mod implementation and preserves its CPU result.
Missing state is omitted and invalid participant counts are not traversed.
Use these records to establish race progress; a surviving process alone is not
proof of a functioning expanded race. This establishes translation and artifact
consistency, not working 24-player gameplay.

The native runtime reads `MkwGuestLayout::kLayout`, defined by the per-game
object library from this generated header. Capacity changes therefore do not
bake a game ABI into a reusable runtime archive. The original twelve-player
fallback is centralized in that glue; expanded builds without a header fail at
compile time. The race-start helper initializes every configured player and uses
the resolved menu/settings offsets, controller-channel count and RNG offsets.
Its twelve-entry character palette repeats cosmetically; storage and iteration
use the full configured player capacity. Native integration tests execute the
real race-start helper at 12, 24 and 48 players and check neighboring memory.

## Rank scoring

`RankTables` requires manifest version 3; reference-only version 1 and array-copy
version 2 manifests remain readable. The USA profile preserves every original
scoring row for one through twelve participants, including when such a field
races under a larger capacity profile. The original-capacity translation remains
unchanged. Expanded translations replace the audited byte lookup with a generated
native helper, so a larger field never reads past the original table.

For larger fields, `Curve` supplies the original twelve-rank anchors
`15,12,10,8,7,6,5,4,3,2,1,0`. The helper linearly interpolates the anchors across
the field, scales points by participant count divided by twelve, and rounds to
the nearest integer. Working backward, each rank receives at least one more
point than the next rank. Equivalently, its score is the maximum of
`interpolated(q) + q - rank` over ranks `q >= rank`. This prevents rounding from
creating ties. The profile's resulting 24-player distribution is:

```text
30,27,24,22,20,18,17,16,15,14,13,12,11,10,9,8,7,6,5,4,3,2,1,0
```

The curve is a profile setting and extends to other field sizes without an extra
twelve-slot table. This is this project's scoring policy, not a claim to reproduce
CTGP-R's implementation. The generated helper uses bounded integer arithmetic,
checks field/rank bounds and needs no new fixed-address guest allocation.
Other scoring and UI consumers still require auditing.

## Finding additional consumers

The audit command follows declared object pointers through control flow and emits
review candidates, without applying patches:

```sh
dotnet run --project translator/src/Translator.Cli -c Release -- \
  audit-guest-layouts 0x8052A1D0 --project projects/mkwii-ntsc-u/recomp.yml \
  --guest-layouts projects/mkwii-ntsc-u/layouts/racedata.json \
  --layout-players 24 --receiver Scenario --out build/layouts/game-type-audit.json
```

`--receiver` declares that the function receives that object type in `r3`;
use it only after verifying the function's contract. The manifest's optional
`GlobalPointers` maps addresses of pointer-valued globals to their object types.
The USA manifest declares the race-data singleton, so functions that load it can
be audited without a receiver declaration. The report includes input hashes,
original instruction words, lifted operands and before/after values. Proposed
references are checked against the original instruction and operand before the
report is written.

The analysis discards volatile pointer facts across guest calls and applies the
helper-effect catalog to host helpers; an unmodeled helper invalidates all GPR
facts. Saved object pointers cross branches and loops only when all reachable
incoming paths agree. Suggestions are collected after fixed-point convergence,
so loop back edges can invalidate an initially plausible pointer. Unresolved
indirect control flow and unsupported instructions prevent inter-block inference.
A variable index multiplied by a declared player-array stride can carry a typed
base through addition. A proposed field must lie inside that declared array,
including through nested scenario fields, and its element stride must be
unchanged. Masked rotates are accepted as a scale only when their mask removes
the wrapped low bits. The report records `IndexStride` for review. Bounds remain a separate
audit; settings outside the indexed array, differing scales, interior cursors and
alignment padding remain unresolved.
Unrelated constants such as controller counts and mode identifiers
are left alone. The report is an aid to manual review, not a completeness proof;
loop bounds, allocation sizes and unproven aliases still need separate auditing.

## Validation

The default translator suite remains independent of game binaries and a host C++
compiler. Layout tests cover twelve-, twenty-four- and forty-eight-player shapes,
invalid layouts, original instruction/operand mismatches, and relocated loads,
stores and loop bounds.

Compiler-backed checks have an explicit integration project. They require Clang
and the pinned USA `Assets/main.dol` and `Assets/StaticR.rel`:

```sh
MKW_TEST_CXX=/absolute/path/to/clang++ \
  dotnet test translator/tests/Translator.IntegrationTests -c Release
```

The game-backed checks verify that thirteen lifecycle/copy/reader/point routines
emit exactly the original C++ at twelve players, and syntax-check their relocated 24-player
versions. A native execution test runs the original scenario assignment at 12,
24 and 48 players against the runtime's actual guest-memory implementation. It
checks every player's selected fields, the settings/mission/ghost tail, preserved
vtables and padding, and guard bytes before and after the destination.
Additional native checks exercise the original mode/team/replay readers and
two-team score aggregation at 12, 24 and 48 players. The aggregation test makes
the final player determine the result, so a truncated twelve-slot loop fails.
The original point updater runs offline battle and both online battle modes at
all three capacities. It checks every player's score and rating, restored stack
pointer, saved integer/floating-point registers, and stack guard bytes. Rating
expectations aggregate the original rating functions' per-opponent results
separately, using varied initial ratings; the updater must include every player
before applying ratings. These battle paths do not rely on the Grand Prix table.
Additional checks run Grand Prix, offline VS and both online VS modes at all three
capacities against independent expected point distributions and per-opponent
rating calculations. These validate point updates, not a running online
race/battle or its network synchronization.
Capacity-count checks vary the number of occupied slots from zero to capacity,
mix local and CPU players, isolate the final slot, and exercise mode-specific
participant and split-screen rules against the expanded settings offsets.
These scans also run at 13 and 100 slots. The original scenario reset and the
real initialization prefix before external parameter-file registration run at
12, 13, 24, 48 and 100 slots, checking every touched player field, neighboring
fields, race/ghost regions and stack guards. This prefix check does not execute
external registration or establish complete game initialization.
Race-to-race bookkeeping checks every player's previous score and finish
position, race-number rollover and neighboring-field guards at all three
capacities. At twelve players, it also compares the complete race-data object,
all GPRs and control registers against execution of the unmodified function.
Native execution checks both branches of a synthetic relocated function. The
growth case inserts 96 arithmetic IR operations while retaining the original
block identities. These checks do not establish that a complete 24-player game
works; that requires the remaining migrations and gameplay tests.

## Address setup inside verified fills

Format version 7 allows `ArrayFills.Addresses` to declare scalar address setup
interleaved with the checked stores. Each entry names a distinct volatile GPR
and one declared array offset. Validation proves the exact base-plus-offset
assignment; destinations may not alias the fill base or value registers.
Expanded emission writes the full arrays, then computes the declared addresses
using their expanded offsets. No other effects are allowed in the span, and
original-capacity emission remains unchanged.
