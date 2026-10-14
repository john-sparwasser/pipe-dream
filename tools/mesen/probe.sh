#!/bin/sh
# macOS/Linux twin of New-MesenProbe.ps1 + Invoke-MesenTest.ps1: prelude.lua + a body, run
# headless, the exit code printed (reference/MESEN.md). PIPEDREAM_MESEN = the Mesen binary
# (on macOS, Mesen.app/Contents/MacOS/Mesen).   Usage: probe.sh <rom> <body.lua> [timeout-sec]
set -eu
dir=$(mktemp -d); lua=$dir/probe.lua                # Mesen wants the .lua extension
{ echo 'local T = (function()'; cat "$(dirname "$0")/prelude.lua"; echo 'end)()'; cat "$2"; } > "$lua"
# Unlike Windows, Mesen here flushes SRAM on stop, and a saved game changes the boot path.
rom=$(basename "$1"); rm -f "$HOME/Library/Application Support/Mesen2/Saves/${rom%.*}.srm" \
    "$HOME/.config/Mesen2/Saves/${rom%.*}.srm"
code=0; "${PIPEDREAM_MESEN:?set PIPEDREAM_MESEN}" --testrunner "$1" "$lua" --timeout="${3:-90}" >/dev/null 2>&1 || code=$?
rm -rf "$dir"; echo "$code"
