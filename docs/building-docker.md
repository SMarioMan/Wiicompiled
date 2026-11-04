# Container builds

Validation status: all 732 translator tests pass. A Linux release built and
booted into CPU-driven VS initialization after being moved, using the null
graphics backend. The Windows x64 target cross-compiles successfully on Linux.
Both complete container workflows were tested using the USA RMCE01 RVZ.
The Linux output also reached CPU-driven VS initialization after being moved. Rendering and Windows gameplay remain untested. LAN, guest-layout
relocation, and 24-player support remain unimplemented.

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
