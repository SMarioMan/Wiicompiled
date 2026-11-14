# Retired Layout Access Auditor

This opt-in diagnostic facility observes guest scalar reads and writes without
changing guest memory or replacing the relocation system. It complements the
static consumer audit; it does not establish complete high-capacity support.

Configure a source runtime build with `MKW_ENABLE_LAYOUT_ACCESS_AUDIT=ON` and
`MKW_USE_PREBUILT_RUNTIME=OFF`. Regenerate translated sources and native layout
constants with the same manifest/capacity. Launch with:

```
--layout-audit=log --layout-audit-report=layout-access-audit.json
```

`--layout-audit=strict` throws before a high-confidence stale write mutates RAM.
It also stops on high-confidence stale reads. `--layout-audit-ambiguous` adds
unattributed overlap leads, which never trigger strict failure. Instrumentation
compiles out of translated and inline memory paths in ordinary builds.

The planning/translation artifacts include `guest_layout_retired_map.json`:
original/expanded sizes, field offsets, extents, counts, strides, current fields
that overlap former storage, and checked relocation references. Generated C++
registers the same descriptors and simple declared load/store field intent.
Composite expressions and arithmetic references are retained in JSON but are not
guessed into field-intent bindings. At 128 racers the current manifest supplies
83 compound types, 3,135 simple access sites and 16 stack-lifetime sites after
the ArchiveMgr migration.

## Ownership and provenance

A watch exists only for a live instance. Native fixtures/wrappers use
`RegisterInstance(type, base, allocatedBytes, lifetime, owner)` and retire its
returned generation at destruction, or use `RetireAllocation(base)` /
`RetireOwner(owner)` at proven allocation/heap lifetime boundaries. Retirement
must precede memory reuse. Register before construction when auditing constructor
accesses. These APIs deliberately do not discover allocations by scanning RAM.

Checked `stwu r1,-Frame.size(r1)` references automatically create frame watches
at their effective write address when the prologue matches the current translated
function entry. Inlined/non-entry prologues lack a proven C++ invocation lifetime;
they are skipped and counted in JSON `unscopedStackEntries`. They must not borrow
the enclosing function's longer lifetime and watch reused stack bytes. Each
tracked translated function invocation owns its
watch, which retires on return or exception. Invocation identity and provenance
are isolated by guest execution context, rather than host thread or CpuContext
address: cooperative guest fibers share those. The guest fiber manager calls `RetireExecutionContext` when a thread exits,
when a guest-thread address is reused, and during shutdown. This also retires
watches when a suspended host stack is destroyed without unwinding C++ scopes.

ArchiveMgr now has automatic constructor/destructor watches through targeted
native wrappers. They delegate to the winning guest translation, register before
its constructor stores and retire after destruction, including non-deleting
destructors and construction failures. The scalar guest delete entry retires
allocation watches before the allocator can reuse an address. Function identities
use the existing region mapping; verified USA entries are `8053A7B0`, `8053A9E0`
and `80229A90`. A missing type descriptor or disabled diagnostics leaves original
construction behavior intact.

Other heap-object families and heap-reset paths still require verified lifetime
integrations. Merely enabling the launch flag does **not** activate every heap type. An empty report with no live
instances is not evidence of coverage. The report includes registered type,
binding, stack-site and currently live-instance counts.

Translated instructions record their original guest PC; translated functions
provide a call stack. Native code can use `ScopedSite` to supply type, field,
function, base and optional index/member offset. `SetPhase` records gameplay
phase supplied by a fixture or runtime integration. Native scalar accesses
participate even without explicit intent, but can then produce only overlap
leads. Resolved-address scalar helpers use their address-preserving fallback while auditing
is enabled. Fused accesses whose individual PC association was lost by optimization
clear their PC instead of borrowing a neighboring instruction's trusted field intent;
they can yield overlap leads, but cannot establish strict stale access. Raw pointers returned by `Memory::GetPointer` and uninstrumented bulk
copies require explicit range observations; pointer acquisition alone is not a
memory read/write. This first version does not claim those paths are covered.

## Interpretation

A checked field-intent access to its former location, outside its current
location, is high confidence when its object instance is unambiguous. An
explicit element index also permits checking computed accesses beyond the old
capacity. Correct current-field accesses are accepted even at a former field's
offset. Multiple overlapping instances of the same type require explicit base
provenance for strict classification.

For a known array consumer, the old stride is also projected over the expanded
count. This catches a stale computed racer index beyond the original twelve
entries when it still lies outside the intended current field. A controlled
translated read at index 23 exercises this path, while a legitimate current-pool
read of exactly the same bytes is accepted. If the old computed address also
lies inside the intended current field, an explicit index is needed to separate
the two interpretations; the auditor does not invent one from an ambiguous address.

Without field intent, an old-region overlap is only an optional lead. It may
be legitimate storage in another current field. No page protection, canaries or
poisoning are applied. No strict action is taken solely on an old-address match.

JSON aggregates by PC, operation, type, field, width and confidence. Address,
base, generation, phase and stack describe the **first sample** for that unique
site; occurrence counts can include later object generations. Reports are
flushed on new unique sites, power-of-two count milestones and strict failures, and on normal runtime exit.
The expected address is null when it cannot be reconstructed safely. Racer
index is reported only for directly per-racer fields, not scalar tail members.
Counter truncation, light exhaustion and valid-address arithmetic mistakes need
independent tests.

## Regression demonstration

`RetiredLayoutAuditTests` compiles deliberately stale and correctly relocated
PPC load/store functions against the real guest memory paths. It checks read
aggregation, strict write prevention, native flat access, legitimate current
storage at a retired offset, ambiguous overlap, guest-fiber provenance, explicit
retirement/reuse and automatic translated stack retirement. Metadata boundaries
cover 12, 13, 24, 25, 26, 48, 52, 64, 100, 116, 118, 127 and 128 racers.

Validation for the initial facility: both controlled regressions passed, all 951
translator unit tests passed, and the existing game-layout audit plus nine Coin
Runners storage cases passed in a 12-case integration run. The generated real
128-racer metadata compiled and registered without duplicate bindings. A source
runtime configured with diagnostics enabled; the engine, memory implementation
and main entry compiled through syntax checks with that build's actual flags.
A complete instrumented game link and gameplay discovery run remain pending,
along with the remaining heap-object lifetime integrations and bulk-access coverage.

The automatic ArchiveMgr lifetime integration passed all 13 capacity fixtures
with the real USA constructor/destructor/array helpers, plus wrapper tests in
both compiled instrumentation modes (15 cases). The wrapper fixture confirms
that original non-deleting/deleting teardown, construction failure and missing
type descriptors retain their intended behavior; release observes no remaining
allocation watch before memory becomes reusable.
