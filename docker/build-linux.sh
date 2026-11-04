#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 2 || ! -f "$1" || ! -d "$2" ]]; then
    echo 'Usage: container GAME_IMAGE EXISTING_OUTPUT_DIRECTORY' >&2
    exit 2
fi
output=$(realpath "$2")
if [[ -n $(ls -A "$output") ]]; then
    echo 'Output directory must be empty; refusing to overwrite existing files.' >&2
    exit 2
fi
source /opt/wiicompiled/docker/build-workspace.sh
prepare_build_workspace
[[ -f aurora-main/CMakeLists.txt ]] || {
    echo 'Initialize submodules before building the image.' >&2
    exit 1
}
dotnet Launcher/WiiCompiled.Setup.Linux/bin/Release/net8.0/WiiCompiled.Setup.Linux.dll \
    install --workspace "$PWD" --game "$1" --install-dir "$output" \
    --cc "$PWD/Launcher/artifacts/portable-tools/toolchain-x86_64/bin/clang" \
    --cxx "$PWD/Launcher/artifacts/portable-tools/toolchain-x86_64/bin/clang++" \
    --cmake "$PWD/Launcher/artifacts/portable-tools/toolchain-x86_64/bin/cmake" \
    --ninja "$PWD/Launcher/artifacts/portable-tools/toolchain-x86_64/bin/ninja" --fuse-ld lld
cp -a Assets/DATA "$output/DATA"
cp LICENSE THIRD-PARTY-NOTICES.md "$output/"
touch "$output/portable.txt"
mkdir -p "$output/UserData"
printf '[paths]\ndvd_root = "../DATA"\n' > "$output/UserData/Config.toml"
