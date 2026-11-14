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
