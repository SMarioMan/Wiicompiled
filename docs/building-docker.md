# Container builds

Validation status: the translator suite and USA production translation pass.
The container recipe is under development; native release compilation, launch,
and movement of the completed release have not yet been validated. Do not treat
it as a tested release pipeline until those checks pass.

## Linux x64

Initialize the pinned graphics submodule before building from the repository root:

```sh
git submodule update --init --recursive
docker build --target builder -f docker/Dockerfile.linux -t wiicompiled-linux .
mkdir -p release-linux
docker run --rm \
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
The build container does not forward a display or GPU. Files produced by Docker
may be owned by root; adjust ownership on the host if necessary before playing,
since portable saves and settings are written under `UserData`.

For environments with a TLS inspection proxy, supply the combined trusted CA
bundle at build time:

```sh
docker build --secret id=proxy_ca,src=/etc/ssl/certs/ca-certificates.crt \
  --target builder -f docker/Dockerfile.linux -t wiicompiled-linux .
```

At runtime, mount the bundle read-only and set `SSL_CERT_FILE` for .NET and
`CMAKE_TLS_CAINFO` for CMake to that mounted path. Preserve the environment's
Docker proxy configuration. Certificates are not copied into image layers.

This recipe currently covers Linux only. Windows packaging and gameplay changes
are separate work; this document does not establish LAN or expanded-player support.

You can also export the release directly with BuildKit, without starting a
container yourself. Use a fresh, empty export destination. The digest both checks the input and invalidates the build
cache when the disc changes. A separate named context keeps the image out of the builder layers;
the exported release necessarily contains extracted game data. Keep that output
and the private build cache local.

```sh
game=/absolute/path/game.rvz
docker build -f docker/Dockerfile.linux --target release \
  --build-context "game=$(dirname "$game")" \
  --build-arg "GAME_FILE=$(basename "$game")" \
  --build-arg "GAME_SHA256=$(sha256sum "$game" | cut -d ' ' -f 1)" \
  --output type=local,dest=release-linux .
```
