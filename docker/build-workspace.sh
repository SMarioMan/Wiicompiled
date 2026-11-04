#!/usr/bin/env bash
# Keep the builder image immutable and allow --user to match the output's owner.
prepare_build_workspace() {
    workspace=$(mktemp -d /var/tmp/wiicompiled.XXXXXXXX)
    trap 'rm -rf -- "$workspace"' EXIT
    cp -a --no-preserve=ownership /opt/wiicompiled/. "$workspace/"
    chmod 700 "$workspace"
    export DOTNET_CLI_HOME="$workspace/.dotnet"
    export XDG_DATA_HOME="$workspace/.local/share"
    mkdir -p "$XDG_DATA_HOME"
    cd "$workspace"
}

# Clean images exclude Launcher/artifacts. Resolve the pinned disc tool before
# extraction instead of relying on a previous installer run's download cache.
ensure_nodtool() {
    if [[ ! -x Launcher/artifacts/nodtool ]]; then
        dotnet build Launcher/WiiCompiled.Setup.Common.Cli -c Release
        dotnet Launcher/WiiCompiled.Setup.Common.Cli/bin/Release/net8.0/WiiCompiled.Setup.Common.Cli.dll \
            --workspace "$PWD"
    fi
}
