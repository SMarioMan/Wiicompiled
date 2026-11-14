# ArchiveMgr capacity migration

Vanilla `ArchiveMgr` owns two `ArchivesHolder[12]` arrays, each with stride
`0x1C`. A high racer index enters the second array or content-archive storage.
MKW100's registry identifies these arrays at `0x8` and `0x158`; its PAL site
list supplies discovery leads. The migration uses verified NTSC-U instructions
and this branch's existing checked layout relocations.

The original USA factory (`8053A710`, allocation operand `8053A72C`) requests
`0x61C`. The constructor (`8053A7B0`) builds both arrays with 12 entries at
`8053A7C8`/`8053A808`. The destructor (`8053A9E0`) similarly destroys 12 entries
at `8053AAB0`/`8053AAC4`. These five operands now use layout size/count symbols.

The manifest retains the original eight-byte header and expands only the two
racer-holder arrays. It then places four content archives (stride `0x24`), seven
load requests (stride `0x54`), the task-thread pointer, the course loader, four
asynchronous kart-loader workers (stride `0x18`) and trailing state after them.
The fixed content/request/worker counts retain their separate roles. At 128,
the derived second-holder offset is `0xE08` and manager size is `0x1F7C`.
Neither expanded value is a runtime literal patch.

Static review covers 63 manager-region entries, including the unnamed loader at
`8053CAF4` and mission loader at `8053B3DC`; reviewing only named `ArchiveMgr::`
methods misses these consumers. The 169 reviewed operand rules move trailing
fields, second-holder bases, computed content accesses and loader-worker bases.
Their original words and semantic operands are checked against the USA image.
Unchanged holder strides and first-array base remain valid; their numerical
index calculations do not need an alternative storage architecture.

`ArchiveManagerStorageTests` exercises the actual factory, manager constructor
and destructor, original array construction/destruction helpers, both holder
lookup families, both file lookup families and content-state lookups. Every
holder ordinal is distinct and initialized/destroyed exactly once. Guards check
the manager boundaries, retained arrays keep their fixed counts, and the entire
lifecycle repeats at a reused allocation. Strict layout auditing is active from
allocation through release in the fixture. Runtime resource construction, task
creation and archive I/O are callbacks; this is not a mixed-roster gameplay test.

The new 13-racer fixture first rejected the original `0x61C` allocation path.
After relocation, all required capacities passed: 12, 13, 24, 25, 26, 48, 52, 64,
100, 116, 118, 127 and 128. The existing static game-layout regression also
passed. A separate inventory regression reproduces all 169 operand candidates
and checks their expanded values against the committed manifest rules.

Remaining qualification: real mixed-character/vehicle archive loading, external
and indirect consumers, pointer-to-holder provenance, heap/resource ownership,
scene teardown and subsequent race startup under the instrumented game. No
complete 128-racer gameplay or 128-console networking claim follows from these
storage tests.

The diagnostic runtime now wraps the verified constructor/destructor and scalar
delete entries. These hooks activate the ArchiveMgr watch before construction
and retire it at destruction/release. The storage fixture uses those same native
wrappers and original NTSC-U translations, rather than registering its allocation
watch itself. A separate wrapper fixture covers deleting/non-deleting teardown,
construction failure, absent layout descriptors, disabled auditing and both
compiled instrumentation modes.

## External singleton and asynchronous request consumers

The first full 128-capacity executable failed before racing even with twelve
active racers. `ArchiveMgr::GetFile(5, "ma_mii_f.brres")` returned null; its archive
holders were still unloaded and the boot-race license check had no loaded save.
The older 24-capacity binary completed the twelve-racer control race with the
same disc tree and NAND. The new failure reproduced with auditing disabled.

An instrumented run with ambiguous leads enabled watched one live ArchiveMgr
and identified `GameScene::enter` at USA `8051670C` writing the old state byte.
That lead prompted a whole-image direct-singleton inventory rather than another
search limited to manager methods. The original-word, typed field audit found
19 missing operands:

- `GameScene::enter/exit`: three writes to the trailing state byte at `+0x60D`.
- Static `ArchiveMgr::LoadArchiveAsync` (`8053AAFC`): eleven accesses to the seven
  request records, originally based at `+0x338`. Its `r3` argument is an archive
  type number, **not a manager receiver**. Giving this entry a manager receiver
  in the earlier audit obscured its computed request index. The unchanged enum
  and `0x54` request stride select the expanded request base correctly once these
  operands move.
- `RootScene::CreateArchiveMgr/CreateInstances`: two course-loader addresses
  originally formed with `+0x588`.
- `GlobeMgr::Update`: a read of the trailing state byte at `+0x60C`.
- `MenuKartModelMgr::Load`: two indexed worker-field reads, originally at
  `+0x5B0` and `+0x5B8`. Four asynchronous workers remain four; only their base
  moves after the racer arrays.

These use the existing checked relocation rules and typed field symbols. The
new global inventory selects a superset by singleton address materialization,
then requires the semantic audit to prove the full address and field derivation;
matching low immediate bits alone never authorizes a patch. Seven additional
consumer entries are recorded without an invented receiver type.

The storage fixture now executes all seven original asynchronous-dispatch
branches, checks every resource-load callback argument, and executes the real
`RootScene::CreateArchiveMgr` pointer setup. Resource I/O and course-loader
initialization remain callbacks. The earlier method inventory now treats the
static dispatcher correctly and includes its eleven request operands. All fifteen archive regressions pass: thirteen guarded lifecycle/dispatch cases
and both static inventories. The rebuilt 128-capacity executable now loads the
save and reaches active twelve-racer VS gameplay with auditing disabled. The
instrumented counterpart reaches race setup but stalls there; its execution
stack is being investigated separately. Indirect consumers, race completion,
and full mixed-resource teardown still require qualification.
