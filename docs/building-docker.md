# Container builds

Validation status: all 915 translator unit tests pass. Complete base and optional LAN
container workflows were tested with the USA RMCE01 RVZ, including Windows x64
cross-compilation inside Linux Docker. Both LAN exports contain the required
release artifacts and matching module source provenance; the Windows import audit
found its application DLL dependencies present, with the VC++ redistributable
included. The moved Linux LAN export passed first-run license creation and
two-instance online VS race progression using the null graphics backend.
Multicast discovery and race progression also passed across two network namespaces
and with four simultaneous local instances. Rendering, Windows gameplay and
completed LAN races remain unverified. Original guest-layout relocation and
24-player support remain unfinished.

## Linux x64

Initialize the pinned graphics submodule before building from the repository root:

```sh
git submodule update --init --recursive
docker build --target builder -f docker/Dockerfile.linux -t wiicompiled-linux .
mkdir -p release-linux
docker run --rm --user "$(id -u):$(id -g)" \
  --mount "type=bind,src=/absolute/path/game.rvz,dst=/input/game.rvz,readonly" \
  --mount "type=bind,src=$(pwd)/release-linux,dst=/output" \
  wiicompiled-linux /input/game.rvz /output
```

The image builds the toolchain and installer; running it validates and extracts
the supplied disc, translates the guest code, and compiles the release. The output
directory must be empty. ISO and other formats supported by the pinned `nodtool`
are also accepted. Supported clean regions are PAL, NTSC-U and NTSC-J; hashes are
checked by the existing installer. Allow at least 20 GB of free space and several
GB of memory. The existing build script sizes compilation pools from CPU and RAM.

The output includes `WiiCompiled`, first-run assets, extracted `DATA`, and a
portable configuration with a relative data path. Move the entire directory
together, then run:

```sh
cd release-linux
./WiiCompiled
```

Run the game on a graphical Linux desktop with Vulkan drivers and the runtime
system libraries appropriate to Ubuntu 24.04 or a compatible newer distribution.
The build container does not forward a display or GPU. The commands above run
with your host UID/GID so portable saves and settings remain writable. Each build
uses a temporary workspace, removed when the container's build script exits.

For environments with a TLS inspection proxy, supply the combined trusted CA
bundle at build time:

```sh
docker build --secret id=proxy_ca,src=/etc/ssl/certs/ca-certificates.crt \
  --target builder -f docker/Dockerfile.linux -t wiicompiled-linux .
```

At runtime, mount the bundle read-only and set `SSL_CERT_FILE` for .NET and
`CMAKE_TLS_CAINFO` for CMake to that mounted path. Preserve the environment's
Docker proxy configuration. Certificates are not copied into image layers.

These recipes build the current base-game player limit and networking behavior.

You can also export the release directly with BuildKit, without starting a
container yourself. Use a fresh, empty export destination. The digest both checks the input and invalidates the build
cache when the disc changes. A separate named context keeps the image out of the builder layers;
the exported release necessarily contains extracted game data. Keep that output
and the private build cache local.

```sh
# Keep this input directory limited to the game image.
game=/absolute/path/game.rvz
docker build -f docker/Dockerfile.linux --target release \
  --build-context "game=$(dirname "$game")" \
  --build-arg "GAME_FILE=$(basename "$game")" \
  --build-arg "GAME_SHA256=$(sha256sum "$game" | cut -d ' ' -f 1)" \
  --output type=local,dest=release-linux .
```

## Windows x64 from Linux

Both Dockerfiles run on Linux x64. Build the shared Linux toolchain image first,
then the Windows cross-builder:

```sh
git submodule update --init --recursive
docker build --target builder -f docker/Dockerfile.linux -t wiicompiled-linux-toolchain .
docker build -f docker/Dockerfile.windows -t wiicompiled-windows-cross .
mkdir -p release-windows
docker run --rm --user "$(id -u):$(id -g)" \
  --mount "type=bind,src=/absolute/path/game.rvz,dst=/input/game.rvz,readonly" \
  --mount "type=bind,src=$(pwd)/release-windows,dst=/output" \
  wiicompiled-windows-cross /input/game.rvz /output 4
```

The last argument limits parallel compilation (default: four jobs). Use an empty
output directory. The Windows image uses a Linux-hosted LLVM-MinGW toolchain,
a native Linux C++/WinRT generator, and Windows SDK metadata. No Windows container,
Wine, or Windows SDK installation is needed to compile. Downloads are checksum
pinned; the WinRT generator's WinMD reader is pinned by its upstream CMake project.
The translator runs on Linux and emits assembly whose syntax follows the compiler
target. Disc identity and both executable hashes are checked before translation.

Copy the complete output directory to a Windows 10/11 x64 desktop with a supported
graphics driver. Install the included Microsoft `VC_redist.x64.exe` if the Visual
C++ 2015–2022 runtime is not already installed, then run `WiiCompiled.exe`.
The Dawn and DirectX shader DLLs depend on this runtime. Keep the DLLs, `DATA`, `portable.txt`,
and `UserData` together. Game data is resolved relative to `UserData/Config.toml`,
and saves/settings remain in that writable directory after moving the release.
The Windows build targets x86-64-v3 CPUs. Windows gameplay validation is still pending; successful cross-compilation
does not establish native Windows runtime behavior.

## Optional LAN product

Build a single LAN variant with `--build-arg PRODUCT=lan` when building either
Dockerfile, or select it when running a builder with `WIICOMPILED_PRODUCT=lan`.
It currently requires the clean USA game and retains twelve-player capacity.
Its discovery protocol is version 2; peers must use matching WiiCompiled LAN
builds with the same capacity.
Every copy of the resulting executable can create or join a room through the
normal LAN/Nintendo WFC menus. Instance identities and startup automation are
runtime options; no host/client builds are needed.

```sh
mkdir -p release-lan
docker run --rm --user "$(id -u):$(id -g)" \
  --env WIICOMPILED_PRODUCT=lan \
  --mount 'type=bind,src=/absolute/path/game.rvz,dst=/input/game.rvz,readonly' \
  --mount "type=bind,src=$(pwd)/release-lan,dst=/output" \
  wiicompiled-linux-toolchain /input/game.rvz /output
```

For Windows, use the same environment option with the Windows cross-builder:

```sh
mkdir -p release-lan-windows
docker run --rm --user "$(id -u):$(id -g)" \
  --env WIICOMPILED_PRODUCT=lan \
  --mount 'type=bind,src=/absolute/path/game.rvz,dst=/input/game.rvz,readonly' \
  --mount "type=bind,src=$(pwd)/release-lan-windows,dst=/output" \
  wiicompiled-windows-cross /input/game.rvz /output 4
```

Each output contains `WiiCompiledLAN` (Linux) or `WiiCompiledLAN.exe` (Windows),
`DATA`, bootstrap files, portable settings, LAN notices and module provenance in
`lan-build`. Windows also includes runtime DLLs and the Visual C++ redistributable.
Linux accepts `WIICOMPILED_BUILD_JOBS`; Windows uses its positional jobs argument.
The Linux BuildKit `release` target also accepts `--build-arg PRODUCT=lan`.

Run the executable without automation flags to use the normal menus. Create a
license in slot zero on first launch. For multiple instances on one machine,
copy the entire release to separate directories and assign unique runtime IDs:

```sh
# From the first portable directory; optionally automate creating a room:
./WiiCompiledLAN --lan-local --lan-id=1 --lan-host
# From a separate copy of the SAME release; optionally join the first host:
./WiiCompiledLAN --lan-local --lan-id=2 --lan-join
# Manual menus can host or join, regardless of ID:
./WiiCompiledLAN --lan-local --lan-id=3
```

Automation ends on reaching the first room. Each process uses its own writable
`UserData`. `--lan-local` binds `127.0.0.ID` for multiple instances on one machine;
use a distinct ID for each copy (currently 1–12).

Without `--lan-local`, the executable selects the default LAN interface and
finds nearby lobbies through IPv4 multicast at `239.255.77.75:27909`, with a
one-hop TTL. No peer list is required. Select an interface explicitly when needed:

```sh
./WiiCompiledLAN --lan-bind=192.168.1.10
# On the other machine:
./WiiCompiledLAN --lan-bind=192.168.1.20
```

Multicast handles discovery; lobby negotiation uses unicast UDP port 27900 and
race packets go directly to the advertised peer endpoints. Permit incoming UDP
traffic for the executable in the host firewall. If the network filters multicast,
add repeatable `--lan-peer=IP` options to both sides for unicast discovery.
These network settings do not restrict who can create a
room. See the [LAN tests](../mods/lan/README.md) for first-run, room and race checks.
Rendering, completed races and Windows LAN gameplay remain unverified;
24-player support is not implemented.
