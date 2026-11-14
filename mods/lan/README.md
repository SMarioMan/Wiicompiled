# LAN port development

This directory contains the LAN v0.9 module adapted for native translation,
including the supplied `build_lan.py` script's per-instance loopback networking,
boot/menu hooks, fake PIDs and client joining. It is under development;
**two-instance room joining and online VS race progression are verified on
Linux; 24-player networking remains incomplete**.

The original archive is `https://www.chadsoft.co.uk/downloads/LAN_MKW_v0.9.zip`,
SHA-256 `2a8d76844e79b23b147ea97f134386d313401e0535c2f52da9ab41822369095e`.
Original module/SDK notices are retained; see [COPYING](source/COPYING).
The attachment SHA-256 is recorded in `PROVENANCE.json`. Game files are not
included. The current boot/menu patch addresses require the verified USA game.

Build the translator and one unified module with the pinned portable toolchain:

```sh
dotnet build translator/Translator.sln -c Release
python3 mods/lan/build.py \
  --toolchain Launcher/artifacts/portable-tools/toolchain-x86_64 --out build/lan
```

The freestanding PowerPC module is linked once. It contains a versioned runtime
configuration record; its ELF symbol generates `lan_runtime_config.h`. Native
startup fills that data record after copying the module image, before guest
hooks run. Runtime configuration changes data, never PowerPC instructions.
There are no compiled host/client roles or instance IDs. Current capacity is
twelve players; 24-player layouts remain unfinished.

Runtime configuration version 2 checks the module's capacity, controller address
and race telemetry offsets against the linked native guest layout. Discovery
protocol 3 advertises capacity and ignores incompatible peers, including earlier
capacity-matched builds with the old SELECT/RH1 wire layouts. Use matching
WiiCompiled LAN builds on every peer; the original Wii LAN v0.9 discovery format
is not compatible with this version.

For development against an explicitly resolved guest layout, pass its translator
context when compiling the module:

```sh
python3 mods/lan/build.py --toolchain /path/to/toolchain \
  --guest-layout-context /path/to/functions/guest_layout_context.json \
  --experimental-guest-layouts --out build/lan-experimental
```

The experimental flag is required while the guest migration is incomplete.
Controller assembly uses the resolved full-width offset. Source C uses the same
capacity and race offsets. This prepares the ABI boundary; it does not establish
24-player lobby, protocol or race support. The current protocol has a 32-slot
SDK ceiling; larger offline storage tests do not establish larger LAN support.

Validated on Linux with the Null renderer: two local instances and two separate
network namespaces discover and race without explicit peers; four simultaneous
local instances join one room and advance together in online VS. Concurrent
server reservations are queued while the SDK finishes its active handshake.
Clean Linux and Windows Docker LAN workflows also exported complete releases
from the USA RVZ. The moved Linux export passed first-run license creation and
two-instance racing; Windows PE imports resolve to shipped DLLs, Windows system
DLLs or the included VC++ redistributable. Completed races, rendering and Windows
LAN gameplay remain unverified.

Default startup preserves the normal menus. Any instance can create a room or
choose a room from the friend roster. Optional `--lan-host` and `--lan-join`
automate the initial connection; they stop imposing a role after the first room.
The default interface is selected automatically. Discovery uses multicast group
`239.255.77.75:27909` with TTL 1; game traffic remains unicast. `--lan-bind=IP`
selects a particular LAN interface, and repeatable `--lan-peer=IP` options add
unicast discovery destinations if multicast is filtered. For multiple processes
on one machine, use `--lan-local --lan-id=N` with distinct IDs and separate
portable directories. PIDs derive from the bound address, independently of lobby
role. See the
[Docker build/run examples](../../docs/building-docker.md#optional-lan-product).

Unlike the original BrainSlug loader, WiiCompiled cannot execute a new native
instruction merely because guest code writes PowerPC bytes. The module's startup
and OSLink instruction writes are therefore removed from C and expressed in
`build.py` as build-time patches. Module references remain symbolic until ELF
linking; pointer halves, original-function calls, BMG trampolines and branches
retain relocation information in `Code.pul`. The original branch target and SDA
offset are captured from verified game inputs before patches are applied.

PADRead is owned by WiiCompiled's native input implementation. The port must
supply menu automation there rather than replace it with the original hardware
routine. The experimental native LAN product provides that integration.

For complete disc-to-release container builds, use the
[Docker LAN recipes](../../docs/building-docker.md#optional-lan-product).
The steps below describe the underlying development workflow.

## Experimental native product

After producing `build/lan/Code.pul`, translate the base with the USA
project so it records the patch set. Then emit the selected mod and shard graph:

```sh
cli=translator/src/Translator.Cli/bin/Release/net8.0/Translator.Cli.dll
project=projects/mkwii-ntsc-u/recomp.yml
dotnet "$cli" translate-recursive 0x800060A4 --project "$project" \
  --outdir generated/functions --output-metadata generated/base_translation_output.json \
  --production-source-bundle generated/base_translation_sources.bin \
  --no-function-files --prune-stale --threads 4
dotnet "$cli" emit-base-manifest --project "$project" --out build/base \
  --functions-dir generated/functions \
  --translation-output-metadata generated/base_translation_output.json --region E
dotnet "$cli" translate-mod --project "$project" --profile lan \
  --base-manifest build/base/mkwii_base_manifest.json \
  --base-translation-output-metadata generated/base_translation_output.json \
  --mod-name 'WiiCompiled LAN' --emit-cpp --threads 4
dotnet "$cli" generate-data-init --project "$project"
dotnet "$cli" emit-build-shards --project "$project" \
  --base-metadata generated/base_translation_output.json \
  --base-functions-dir generated/functions --native-source-dir runtime/src \
  --resolved-profile "build/mods/lan/resolved_dispatch_profile.json" \
  --retro-cpp-dir "build/mods/lan/cpp" --out generated/build_shards
cmake -S runtime -B native-build -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_COMPILER="$TOOLCHAIN/bin/clang" \
  -DCMAKE_CXX_COMPILER="$TOOLCHAIN/bin/clang++" \
  -DMKW_MOD_PRODUCT=lan -DMKW_LAN_CONFIG_HEADER="$PWD/build/lan/lan_runtime_config.h"
cmake --build native-build --target WiiCompiledLAN --parallel 4
```

`TOOLCHAIN` must point to the pinned portable LLVM toolchain. Native prerequisites
are the same as the base product; the Linux Docker builder supplies them. The
shard emitter retains some historical `retro` option names for the common mod
pipeline. The resulting product identifies itself as WiiCompiled LAN and does
not enable Retro Rewind's network or filesystem behavior.

Copy the same executable into separate portable directories with `DATA`, `wii_bootstrap`,
`dsp_coef.bin` and the other bootstrap files exported by the base Docker build.
Use `portable.txt` and separate `UserData` directories; set `[paths] dvd_root`
to `"../DATA"`. Never share a writable NAND between instances.

Launch `WiiCompiledLAN` normally, or use `WiiCompiledLAN --headless` to hide the
window and silence audio during automated testing. A working graphics/display
backend is still needed. The unified Linux executable and its Windows x64
cross-build inside Linux Docker have succeeded;
startup reads the supplied boot arguments. Testing has reached
two instances joining one room on one Linux machine with separate portable
NANDs. Host state 3 and client state 4 each reported two players. This was
observed using Xvfb and the null renderer. The automated race test also completed
character/kart/drift selection and voting, reached the friend VS race on Luigi
Circuit (course 8, mode 7), and observed stage 2 with advancing frames on both
peers. Identical executables also passed with runtime IDs 1 and 2 exchanging
host/join roles. Manual menu hosting and joining passed with automation disabled.
Two isolated network namespaces at `172.30.241.10` and `172.30.241.20` also reached
and advanced the same race using explicit runtime LAN peer addresses. Rendered
races, race completion and Windows LAN gameplay remain unverified.


## Profiles and local integration test

Each instance needs a valid license in slot zero. If no license exists, the
module returns to the normal first-run menu instead of using the debug online
boot path. Create the license through the game, then restart the instance.
The native menu driver accepts the LAN data prompt and stops providing automatic
input after reaching the friend room. Normal first-run license creation has also
been exercised through controller input with an initially empty license slot.
For a fresh private portable product, this automated check presses the normal
menu controls, then requires an on-disk license with a valid save CRC:

```sh
python3 mods/lan/tests/create_profile.py --product '/absolute/path/LAN Host'
```

It refuses an existing license in slot zero. It uses the default Mii selected by
the game and writes its game log to `lan-profile-game.log`. Restart the product
after creation to enter the LAN boot path.

With a working display (or Xvfb) and two prepared portable products, run:

```sh
python3 mods/lan/tests/local_pair.py \
  --host '/absolute/path/LAN Host' --client '/absolute/path/LAN Client'
```

The test requires identical executable hashes in all directories. It passes
`--lan-local`, runtime IDs and automation flags; use `--host-id=2 --client-id=1`
to reverse the roles with the same executable. Repeat `--client` for additional
independent portable directories (the default product currently supports twelve
slots). The test driver accepts `--capacity N` for an experimental matched build
and validates runtime IDs against that capacity.
For many local instances on a constrained host, `--startup-gap 12` staggers
bootstrap work. `--host-first` waits for the host to create its active room,
then starts all clients together to exercise concurrent admission without
competing with host initialization. Alternatively, `--sequential-join` waits for all launched
instances to report the expected active room before starting the next client;
it fails on a join error dialog instead of silently retrying. This exercises
incremental room admission separately from simultaneous joins.
`--menu-pulse 2` can hold test inputs longer on very slow local hosts; the
default remains 0.2 seconds. The first selection pulse starts immediately when
the room checkpoint passes so socket scheduling cannot consume it beforehand.
`--telemetry-timeout 90` can explicitly allow slower telemetry;
the default freshness window remains three seconds. This changes only the test
observer's tolerance: all peers must still report the full room, the same course
and at least 120 advancing simulation frames. It does not extend game network
timeouts or establish acceptable real-time performance.
Optional repeated `--client-id` values follow the client directory order;
otherwise the test assigns distinct IDs automatically. Run local processes in
the same network namespace. Discovery uses multicast, with no peer list supplied.

The test checks unique source addresses, host/client states, the expected player
count on every peer and five seconds in the active room. It exits nonzero on
process exit, timeout or address mismatch, writes `lan-test-game.log` in each
product, and terminates its processes on exit. Every thirty seconds it also
prints the latest room observation and its age for each peer, including missing
peers, so an unchanged room can be distinguished from missing telemetry. Add `--race` to drive the host's
Start button, character/kart/drift selection and course voting through controller
input:

```sh
python3 mods/lan/tests/local_pair.py --race \
  --host '/absolute/path/LAN Host' \
  --client '/absolute/path/LAN Client 1' --client '/absolute/path/LAN Client 2'
```

The race check requires every game's in-race scenario to contain the expected
player count on the same course, reach racing stage 2, and advance at least 120
simulation frames each over five seconds. Before pressing Start, the driver also
requires fresh `MKWN` SDK telemetry from every peer: all expected members,
no suspension already active, the host waiting for clients, and every client
finished with its mesh reservations. Every peer must also report the same
direct-connection bitmap containing the entire room; an idle SDK state can
occur between individual peer negotiations. The room's displayed player count can
reach the final count while those connections are still being negotiated;
starting then can leave clients unable to request suspension. SDK states and
observation ages are included in the periodic progress records.
Use `--mesh` to stop after every peer has complete SDK connections, keeping
lobby benchmarks independent of character/course menus and race loading.
`--race` includes the same connection barrier before starting the race.

Room and advancing-race pass records include elapsed wall time from driver
startup; the complete-mesh checkpoint has its own elapsed time. Compare these
separately when benchmarking large-room joins. The room check includes a
five-second stability interval and the race check includes menus/loading, so
neither is a pure handshake measurement. Use repeated normal-clock runs on the
same hosts and builds for performance comparisons; keep clock-scaled functional
checks separate.

Queued SDK reservations resume in both idle states: `SV_WAITING` for the room
host and `CL_WAITING` for clients that also accept mesh links. Draining only in
the host state leaves requests queued indefinitely on clients. The native
`test_pending_reservations.py` fixture executes the actual queuing and polling
code, including busy states, expiry, and one resumed request per poll.
Requests now keep their original queue age across duplicate retries. Idle
arrivals enter the same queue, so new datagrams cannot bypass waiting peers;
the oldest queued tick wins, with round-robin selection for ties. Storage is
bounded to one request per discovered peer, stale requests expire, and dispatch
remains limited to one reservation per poll. Other status commands and crossed
outgoing client negotiations retain their immediate SDK handling. The native
fixture also checks slot bias, retry ordering, idle-arrival bypass, malformed
records and shutdown. Live timings must establish the performance impact.

The race check uses separate `MKWR` telemetry
packets; the original `MKWL` room-status packet stays compatible. This test passed
with both cleaned native Linux builds. It does not verify finishing a race,
rendering, item synchronization or battle modes.

For controller-driven integration tests, `WIICOMPILED_TEST_PAD_FILE` opts into a
regular input file, replaced atomically by the test driver. Bytes 0–1 hold a
big-endian GameCube button mask; optional bytes 2–3 hold signed stick X/Y values.
A two-byte record centers the stick. Four zero bytes release every injected
control. A missing or truncated record supplies no input. This hook is disabled
unless the environment variable is set. For example, `01 00 00 00` presses A,
`00 00 50 00` moves the stick right, and `00 00 00 00` releases it.
`--diagnostics` on the module builder additionally logs unique original BMG
message IDs to help identify menu prompts and enables SDK error, warning,
matching and transport logs. The runtime forwards the formatted console output,
including the detailed SDK message bodies.

The [two-peer capacity-24 trace](tests/results/two-peer-capacity24.json) records a
Linux test of the uniform experimental layout with ABI 2. Runtime IDs 24 and 13
used the same executable, discovered each other over multicast, entered one
lobby, selected course 8, and advanced together in racing stage 2 with exactly
two racers. This verifies the expanded module/native boundary and removal of
unused CPU slots; it does not demonstrate 24 active racers. The five Python
layout/ELF checks and native capacity-option tests also pass.

The [four-peer capacity-24 trace](tests/results/four-peer-capacity24.json) repeats
the check after the USER and SELECT cache migrations. All four isolated local
instances share one lobby and advance in the same four-racer online VS race.
It has the same finish/rendering/item/battle limitations as the two-peer check.

The [room-command regression trace](tests/results/room-packet-regression-capacity24.json)
records the restored two-peer VS transition after relocating queue-relative
pending packet state. It includes voting and advancing race simulations, but
does not establish a full 24-player race.

For functional testing on a host that cannot run many real-time instances,
`MKW_TEST_CLOCK_DIVISOR=16` explicitly slows the guest time base and audio-device
poll time. It is accepted only with `--headless`; the default divisor is one.
The LAN test driver labels modified-clock traces and pass records. All peers
must use the same setting. This is a diagnostic aid, not evidence of default
network timing or acceptable real-time performance. Keep default-timing checks
separate; do not use this setting to claim a release's real-time network limit.
The setting does not change the emulated VI/frame counter or physics updates.

The standalone clock guard/rounding checks can be run with:

```sh
c++ -std=c++20 -Iruntime/include runtime/tests/headless_test_clock_tests.cpp \
  -o /tmp/headless-test-clock-tests
/tmp/headless-test-clock-tests
```

For headless finish/results automation, `MKW_TEST_LAN_CPU=1` lets the original
CPU driver control real local racers after the normal LAN menus. It requires the
LAN product and `--headless`, and cannot be combined with direct `--race` boot.
It retains the local network player type, leaves remote racers under network
control, preserves race RNG/settings and keeps a controller for menu input.
Native layout fixtures cover local/remote selection, channel assignment and RNG
at 12/24/48 capacity. This opt-in test facility does not establish a completed
24-console race; finish and results must be observed from the simulation.

`local_pair.py --finish` includes the race transition check, enables local CPU
driving in each child and waits for every peer's native simulation log to report
all active racers finished. It checks the actual field size, observed racers,
finish flags, stage and frame count; menu state alone cannot pass it. Inputs stop
advancing menus after racing begins. This checks finishing, not results/menu
return. Increase `--timeout` to cover a full race. A prototype of this check
completed a normal-timing three-lap two-console LAN race in 207.10 seconds; each
console observed both racers finished.

The reservation queue also uses a bounded allowance for larger discovered fields.
The USA SDK initializes its reservation deadline to 6,000 ms at `800DEE14`;
`DWCi_MatchProcess` reads that field at `800D6428` and `800D6470`. While waiting
for a reservation, the LAN poll retains six seconds through twelve live peers
and allows two seconds per additional recently discovered peer, capped by build
capacity and the 32-console SDK limit (46 seconds maximum). An existing longer
allowance is retained. Disabled/short SDK timers, other states, the original
timer origin and retry count are preserved. Native policy checks pass at
12/13/24/32 capacity. Live comparison is required before attributing improved
joining reliability or speed to this allowance.

The driver fails immediately on a game's error dialog during the main join/race
loop. Finish checks require matching course and mode, retain two seconds of
process liveness after complete finish evidence, and report any child exit as a
failure. Normal race-progress checks also require every peer's mode to match.

Larger fields also use a constant-work mesh deadline calculation. The retail
`DWCi_GetNextMeshMakeTryTick` repeatedly scans the SDK connection list and adds
multi-second delays based on existing connections and node position. Two scoped
call-site shims pass their candidate node from r31 to the LAN scheduler. Stock
twelve-capacity builds and unsupported states retain the original SDK routine.

Expanded builds use this policy from three participants onward, so legacy
deadlines cannot accumulate during the initial lobby growth. Stable PID ordering
chooses a preferred initiator for each pair. Pair-specific jitter spreads attempts; the opposite endpoint has a grace
period before it can initiate. Retry backoff is bounded, and the SDK keeps its
own connection checks, attempt limits and teardown. The mesh still has 276 links
at 24 consoles. The shims preserve LR and use symbolic linker/patch targets;
the original no-argument SDK function remains available to other callers.
Native tests cover ordering, jitter, bounded backoff and stock fallback. Linked
PowerPC checks verify both scoped sites, the candidate register and SDK import.
Live timing and reliability comparisons remain required.

Fast mesh attempts require routes before multicast discovery finishes. Incoming
status packets now teach the control sender's actual endpoint after checking its
version, game port and PID/address relationship. This does not advertise a lobby
or invent Mii data. Outgoing status traffic can use a matching SDK roster entry's
private address when the discovery cache is late. The shared SDK public address
is a NAT placeholder and is never used as a fallback route. Reservation allowances
also account for the SDK's known participant count while discovery catches up.

The non-preferred endpoint gives the preferred initiator thirty seconds before
starting a fallback attempt, with subsequent backoff capped at sixty seconds.
This avoids the crossed reservation waits seen with the earlier short grace.
The preferred endpoint retains its short, jittered deadline. A corrected
normal-timing thirteen-console candidate reached full connectivity at 37.40 s;
normal-timing 24-console joining and race completion still require validation.
