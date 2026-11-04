#!/usr/bin/env bash
set -euo pipefail
if [[ $# -lt 2 || $# -gt 3 || ! -f "$1" || ! -d "$2" ]]; then
    echo 'Usage: container GAME_IMAGE EMPTY_OUTPUT_DIRECTORY [JOBS]' >&2
    exit 2
fi
game=$(realpath "$1")
output=$(realpath "$2")
jobs=${3:-4}
[[ "$jobs" =~ ^[1-9][0-9]*$ ]] || { echo 'JOBS must be a positive integer.' >&2; exit 2; }
[[ -z $(ls -A "$output") ]] || { echo 'Output directory must be empty.' >&2; exit 2; }
case "${WIICOMPILED_PRODUCT:-base}" in
    base) ;;
    lan) exec bash /opt/wiicompiled/docker/build-lan-release.sh "$1" "$2" windows "$jobs" ;;
    *) echo 'WIICOMPILED_PRODUCT must be base or lan.' >&2; exit 2 ;;
esac
source /opt/wiicompiled/docker/build-workspace.sh
prepare_build_workspace
ensure_nodtool
source Launcher/build-cache.sh
mkdir -p Assets
[[ ! -d Assets/DATA ]] || { echo 'Unexpected existing disc extraction.' >&2; exit 1; }
Launcher/artifacts/nodtool extract "$game" Assets/DATA -q
cp Assets/DATA/sys/main.dol Assets/main.dol
cp Assets/DATA/files/rel/StaticR.rel Assets/StaticR.rel
project=$(mkw_resolve_project "$PWD" "$PWD/Assets")
native=$PWD/Launcher/artifacts/portable-tools/toolchain-x86_64/bin
bash Launcher/local-build.sh --output-dir "$output" --translate-only --parallel "$jobs" \
    --cc "$native/clang" --cxx "$native/clang++" --cmake "$native/cmake" --ninja "$native/ninja"
build=$PWD/build-windows-cross
trust_args=()
[[ -z ${SSL_CERT_FILE:-} ]] || trust_args+=("-DCMAKE_TLS_CAINFO=$SSL_CERT_FILE")
"$native/cmake" -S runtime -B "$build" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$PWD/docker/windows-x64.cmake" \
    -DCMAKE_MAKE_PROGRAM="$native/ninja" -DCMAKE_BUILD_TYPE=Release \
    -DMKW_CPPWINRT_INCLUDE_DIR=/opt/windows-cross/include \
    -DMKW_TRANSLATED_COMPILE_JOBS="$jobs" -DMKW_X86_CPU_PROFILE=v3 \
    -DCMAKE_POLICY_DEFAULT_CMP0168=NEW "${trust_args[@]}"
"$native/cmake" --build "$build" --target WiiCompiled --parallel "$jobs"
cp "$build/WiiCompiled.exe" "$output/"
# CMake collects the target's MinGW, SDL and graphics runtime DLLs beside the executable.
find "$build" -maxdepth 1 -name '*.dll' -exec cp -t "$output" {} +
cp /opt/windows-cross/redist/VC_redist.x64.exe "$output/"
cp LICENSE THIRD-PARTY-NOTICES.md "$output/"
for name in dsp_coef.bin initial_pipeline_cache.db noshaders.zip; do
    [[ ! -f "$build/$name" ]] || cp "$build/$name" "$output/"
done
[[ ! -d "$build/wii_bootstrap" ]] || cp -a "$build/wii_bootstrap" "$output/"
cp -a Assets/DATA "$output/DATA"
touch "$output/portable.txt"
mkdir -p "$output/UserData"
printf '[paths]\ndvd_root = "../DATA"\n' > "$output/UserData/Config.toml"
printf 'game_id=%s\ndol_sha256=%s\nrel_sha256=%s\n' \
    "$(mkw_project_pin "$project" game_id)" "$(mkw_sha256 Assets/main.dol)" \
    "$(mkw_sha256 Assets/StaticR.rel)" > "$output/build-inputs.txt"
