#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 4 || ! -f "$1" || ! -d "$2" ]]; then
    echo 'Usage: build-lan-release.sh GAME_IMAGE EMPTY_OUTPUT_DIRECTORY {linux|windows} JOBS' >&2
    exit 2
fi
game=$(realpath "$1")
output=$(realpath "$2")
platform=$3
jobs=$4
[[ "$platform" == linux || "$platform" == windows ]] || { echo 'Invalid target platform.' >&2; exit 2; }
[[ "$jobs" =~ ^[1-9][0-9]*$ ]] || { echo 'JOBS must be positive.' >&2; exit 2; }
[[ -z $(ls -A "$output") ]] || { echo 'Output directory must be empty.' >&2; exit 2; }
source /opt/wiicompiled/docker/build-workspace.sh
prepare_build_workspace
ensure_nodtool
source Launcher/build-cache.sh
mkdir -p Assets
[[ ! -d Assets/DATA ]] || { echo 'Unexpected existing disc extraction.' >&2; exit 1; }
# Keep one extracted copy even when /output is a separate bind mount.
Launcher/artifacts/nodtool extract "$game" "$output/DATA" -q
ln -s "$output/DATA" Assets/DATA
cp Assets/DATA/sys/main.dol Assets/main.dol
cp Assets/DATA/files/rel/StaticR.rel Assets/StaticR.rel
project=$(mkw_resolve_project "$PWD" "$PWD/Assets")
[[ $(mkw_project_pin "$project" game_id) == RMCE01 ]] || {
    echo 'The experimental LAN product currently requires the clean USA game.' >&2; exit 1;
}
native=$PWD/Launcher/artifacts/portable-tools/toolchain-x86_64/bin
cli=$PWD/translator/src/Translator.Cli/bin/Release/net8.0/Translator.Cli.dll
dotnet build translator/src/Translator.Cli/Translator.Cli.csproj -c Release
python3 mods/lan/build.py --toolchain "$(dirname "$native")" --out build/lan
bash Launcher/local-build.sh --output-dir "$output" --translate-only --parallel "$jobs" \
    --translator-dll "$cli" --cc "$native/clang" --cxx "$native/clang++" \
    --cmake "$native/cmake" --ninja "$native/ninja"
dotnet "$cli" translate-mod --project "$project" --profile "lan" \
    --base-manifest build/base/mkwii_base_manifest.json \
    --base-translation-output-metadata generated/base_translation_output.json \
    --mod-name 'WiiCompiled LAN' --region E --emit-cpp --threads "$jobs"
dotnet "$cli" emit-build-shards --project "$project" \
    --base-metadata generated/base_translation_output.json --base-functions-dir generated/functions \
    --native-source-dir runtime/src --resolved-profile "build/mods/lan/resolved_dispatch_profile.json" \
    --retro-cpp-dir "build/mods/lan/cpp" --out generated/build_shards
build=$PWD/native-build-lan
configure=(-S runtime -B "$build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_MAKE_PROGRAM="$native/ninja" -DMKW_MOD_PRODUCT=lan -DMKW_LAN_CONFIG_HEADER="$PWD/build/lan/lan_runtime_config.h" \
    -DMKW_TRANSLATED_COMPILE_JOBS="$jobs" -DMKW_X86_CPU_PROFILE=v3 \
    -DCMAKE_EXE_LINKER_FLAGS=-fuse-ld=lld)
[[ -z ${SSL_CERT_FILE:-} ]] || configure+=("-DCMAKE_TLS_CAINFO=$SSL_CERT_FILE")
if [[ "$platform" == windows ]]; then
    configure+=(-DCMAKE_TOOLCHAIN_FILE="$PWD/docker/windows-x64.cmake" \
        -DMKW_CPPWINRT_INCLUDE_DIR=/opt/windows-cross/include -DCMAKE_POLICY_DEFAULT_CMP0168=NEW)
    binary=WiiCompiledLAN.exe
else
    configure+=(-DCMAKE_C_COMPILER="$native/clang" -DCMAKE_CXX_COMPILER="$native/clang++")
    binary=WiiCompiledLAN
fi
"$native/cmake" "${configure[@]}"
"$native/cmake" --build "$build" --target WiiCompiledLAN --parallel "$jobs"
cp "$build/$binary" "$output/"
if [[ "$platform" == windows ]]; then
    find "$build" -maxdepth 1 -name '*.dll' -exec cp -t "$output" {} +
    cp /opt/windows-cross/redist/VC_redist.x64.exe "$output/"
fi
for name in dsp_coef.bin initial_pipeline_cache.db noshaders.zip cacert.pem; do
    [[ ! -f "$build/$name" ]] || cp "$build/$name" "$output/"
done
cp -a "$build/wii_bootstrap" "$output/"
cp LICENSE THIRD-PARTY-NOTICES.md "$output/"
cp mods/lan/source/COPYING "$output/LAN-COPYING"
mkdir "$output/lan-build"
cp "build/lan/Code.pul" "build/lan/"*.json "$output/lan-build/"
touch "$output/portable.txt"
mkdir -p "$output/UserData"
printf '[paths]\ndvd_root = "../DATA"\n' > "$output/UserData/Config.toml"
printf 'game_id=RMCE01\nlan_runtime_configuration=1\ndol_sha256=%s\nrel_sha256=%s\n' \
    "$(mkw_sha256 Assets/main.dol)" "$(mkw_sha256 Assets/StaticR.rel)" \
    > "$output/build-inputs.txt"
