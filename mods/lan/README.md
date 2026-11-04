# LAN port development

This directory contains the LAN v0.9 module adapted for native translation,
including the supplied `build_lan.py` script's per-instance loopback networking,
boot/menu hooks, fake PIDs and client joining. It is under development;
**multi-instance gameplay and 24-player support are not yet verified**.

The original archive is `https://www.chadsoft.co.uk/downloads/LAN_MKW_v0.9.zip`,
SHA-256 `2a8d76844e79b23b147ea97f134386d313401e0535c2f52da9ab41822369095e`.
Original module/SDK notices are retained; see [COPYING](source/COPYING).
The attachment SHA-256 is recorded in `PROVENANCE.json`. Game files are not
included. The current boot/menu patch addresses require the verified USA game.

Build the translator and use the repository's pinned portable LLVM toolchain:

```sh
dotnet build translator/Translator.sln -c Release
python3 mods/lan/build.py \
  --toolchain Launcher/artifacts/portable-tools/toolchain-x86_64 \
  --instance 1 --instances 2 --out build/lan-host
python3 mods/lan/build.py \
  --toolchain Launcher/artifacts/portable-tools/toolchain-x86_64 \
  --instance 2 --instances 2 --out build/lan-client
```

The compiler runs on Linux and emits freestanding PowerPC ELF; a guest compiler
is needed only at build time. These commands produce `Code.pul`, ELF, static
patch JSON and source/compiler/link provenance, not standalone executables.
Use different IDs for every process: ID 1 hosts, other IDs join. Each instance
binds `127.0.0.ID:27900` for discovery and `127.0.0.ID:27901` for game traffic.
The optional status monitor receives packets at `127.0.0.254:27900`.
Current IDs/counts are limited to the original game's twelve-player layout.

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

## Experimental native product

After producing both profiles' `Code.pul` files, translate the base with the USA
project so it records both patch sets. Then emit the selected mod and shard graph:

```sh
cli=translator/src/Translator.Cli/bin/Release/net8.0/Translator.Cli.dll
project=projects/mkwii-ntsc-u/recomp.yml
role=host                         # client for the second executable
instance=1                        # 2 for the second executable
dotnet "$cli" translate-recursive 0x800060A4 --project "$project" \
  --outdir generated/functions --output-metadata generated/base_translation_output.json \
  --production-source-bundle generated/base_translation_sources.bin \
  --no-function-files --prune-stale --threads 4
dotnet "$cli" emit-base-manifest --project "$project" --out build/base \
  --functions-dir generated/functions \
  --translation-output-metadata generated/base_translation_output.json --region E
dotnet "$cli" translate-mod --project "$project" --profile "lan-$role" \
  --base-manifest build/base/mkwii_base_manifest.json \
  --base-translation-output-metadata generated/base_translation_output.json \
  --mod-name 'WiiCompiled LAN' --emit-cpp --threads 4
dotnet "$cli" generate-data-init --project "$project"
dotnet "$cli" emit-build-shards --project "$project" \
  --base-metadata generated/base_translation_output.json \
  --base-functions-dir generated/functions --native-source-dir runtime/src \
  --resolved-profile "build/mods/lan-$role/resolved_dispatch_profile.json" \
  --retro-cpp-dir "build/mods/lan-$role/cpp" --out generated/build_shards
cmake -S runtime -B native-build -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_COMPILER="$TOOLCHAIN/bin/clang" \
  -DCMAKE_CXX_COMPILER="$TOOLCHAIN/bin/clang++" \
  -DMKW_MOD_PRODUCT=lan -DMKW_LAN_INSTANCE="$instance"
cmake --build native-build --target WiiCompiledLAN --parallel 4
```

`TOOLCHAIN` must point to the pinned portable LLVM toolchain. Native prerequisites
are the same as the base product; the Linux Docker builder supplies them. The
shard emitter retains some historical `retro` option names for the common mod
pipeline. The resulting product identifies itself as WiiCompiled LAN and does
not enable Retro Rewind's network or filesystem behavior.

The module's `--instance` and CMake's `MKW_LAN_INSTANCE` must match. Copy each
executable into its own release directory with `DATA`, `wii_bootstrap`,
`dsp_coef.bin` and the other bootstrap files exported by the base Docker build.
Use `portable.txt` and separate `UserData` directories; set `[paths] dvd_root`
to `"../DATA"`. Never share a writable NAND between instances.

Launch `WiiCompiledLAN` normally, or use `WiiCompiledLAN --headless` to hide the
window and silence audio during automated testing. A working graphics/display
backend is still needed. Linux host and client native builds have succeeded;
startup now reads the supplied boot arguments. Testing currently exposes a
video-retrace scheduler stall before room discovery, so these builds are not
yet a working LAN release.
