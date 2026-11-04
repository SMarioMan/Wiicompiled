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
