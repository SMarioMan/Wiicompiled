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
menu and awards scenarios and audits 203 references across creation, construction,
destruction, scenario assignment, selected mode/menu readers, capacity scans and
point updates.
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
players. The CLI rejects expanded builds from an incomplete manifest.

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

Exact division by a positive integer expresses unrolled loop counts, such as
`players/4` for the original four-player unrolled capacity scan. A capacity that
does not divide exactly is rejected rather than silently skipping or overrunning
slots. The current development manifest therefore requires multiples of four.
This scan counts the expanded array while preserving the separate physical
four-screen limit and the original competition-mode participant limits.

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

The audit command follows declared object pointers within basic blocks and emits
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

The analysis discards volatile pointer facts across guest calls and does not
guess dynamic indices, interior array cursors, alignment padding or inter-block
merges. A receiver unchanged throughout a call-free function can be carried into
each block. Unrelated constants such as controller counts and mode identifiers
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

The game-backed checks verify that twelve twelve-player lifecycle/copy/reader/point routines emit
exactly the same C++ as the originals, and syntax-check their relocated 24-player
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
Race-to-race bookkeeping checks every player's previous score and finish
position, race-number rollover and neighboring-field guards at all three
capacities. At twelve players, it also compares the complete race-data object,
all GPRs and control registers against execution of the unmodified function.
Native execution checks both branches of a synthetic relocated function. The
growth case inserts 96 arithmetic IR operations while retaining the original
block identities. These checks do not establish that a complete 24-player game
works; that requires the remaining migrations and gameplay tests.
