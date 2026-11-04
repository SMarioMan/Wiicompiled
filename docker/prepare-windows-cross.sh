#!/usr/bin/env bash
set -euo pipefail
[[ $(uname -m) == x86_64 ]] || { echo 'The Windows cross-builder requires Linux x64.' >&2; exit 1; }
native=/opt/wiicompiled/Launcher/artifacts/portable-tools/toolchain-x86_64/bin
root=/opt/windows-cross
mkdir -p /opt/cross-downloads "$root"
fetch() {
    local name=$1 digest=$2 url=$3
    local path=/opt/cross-downloads/$name
    if [[ ! -f "$path" ]] || ! printf '%s  %s\n' "$digest" "$path" | sha256sum -c - >/dev/null; then
        curl -fL "$url" -o "$path.partial"
        printf '%s  %s\n' "$digest" "$path.partial" | sha256sum -c -
        mv "$path.partial" "$path"
    fi
}
fetch mingw.tar.xz 534b92e067b22a6b4441f48ae9240a3341b17825d04d577eab0cf85c44b4deda \
    https://github.com/mstorsjo/llvm-mingw/releases/download/20260616/llvm-mingw-20260616-ucrt-ubuntu-22.04-x86_64.tar.xz
fetch cppwinrt.tar.gz 7f4fca7d63046d0a8ea35c586836c74eab1492ef34ea227ec5c4e50be6c35e7d \
    https://codeload.github.com/microsoft/cppwinrt/tar.gz/9bb8a57e46aea546b70b0af73e2b4137b8fed759
fetch contracts.nupkg 933d0aee3bf2ec5ad04d2e19d27609a95eecec4d3ff0f50b222e2e3931819ef9 \
    https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.contracts/10.0.26100.1/microsoft.windows.sdk.contracts.10.0.26100.1.nupkg
fetch vc_redist.x64.exe cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b \
    https://download.visualstudio.microsoft.com/download/pr/990c720a-46ce-4dc7-87b9-c2eb615d34b9/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe
mkdir -p "$root/redist"
cp /opt/cross-downloads/vc_redist.x64.exe "$root/redist/VC_redist.x64.exe"
mkdir -p "$root/mingw" "$root/cppwinrt-source" "$root/metadata"
tar -xf /opt/cross-downloads/mingw.tar.xz -C "$root/mingw" --strip-components=1
python3 docker/mingw-header-overlay.py "$root/mingw/x86_64-w64-mingw32/include" "$root/headers.json"
tar -xzf /opt/cross-downloads/cppwinrt.tar.gz -C "$root/cppwinrt-source" --strip-components=1
python3 - <<'PY'
from pathlib import Path
import zipfile
with zipfile.ZipFile('/opt/cross-downloads/contracts.nupkg') as archive:
    for name in archive.namelist():
        if name.endswith('.winmd'):
            (Path('/opt/windows-cross/metadata') / Path(name).name).write_bytes(archive.read(name))
PY
"$native/cmake" -S "$root/cppwinrt-source" -B "$root/cppwinrt-build" -G Ninja \
    -DCMAKE_MAKE_PROGRAM="$native/ninja" -DCMAKE_CXX_COMPILER="$native/clang++" \
    -DCMAKE_BUILD_TYPE=Release -DCPPWINRT_BUILD_VERSION=3.0.0.0
"$native/cmake" --build "$root/cppwinrt-build" --parallel 4
"$root/cppwinrt-build/cppwinrt" -input "$root/metadata" -output "$root/include"
dotnet run --project Launcher/WiiCompiled.Setup.Common.Cli -c Release -- --workspace /opt/wiicompiled
