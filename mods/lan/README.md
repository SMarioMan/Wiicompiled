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
