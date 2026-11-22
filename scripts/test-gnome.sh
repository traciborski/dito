#!/usr/bin/env bash
set -euo pipefail

archive=${1:?Usage: bash scripts/test-gnome.sh EXTENSION.zip}
root=$(cd "$(dirname "$0")/.." && pwd -P)
runtime=$(mktemp -d)
trap 'rm -r -- "$runtime"' EXIT

env -u WAYLAND_DISPLAY -u WAYLAND_SOCKET -u DISPLAY \
    XDG_SESSION_TYPE=wayland XDG_RUNTIME_DIR="$runtime" DITO_GNOME_TEST=1 LIBGL_ALWAYS_SOFTWARE=1 \
    DITO_GNOME_NATIVE_COPY="$root/tests/Dito.Gnome.Tests/native-copy.py" \
    timeout --kill-after=5s 60s dbus-run-session --config-file="$root/tests/Dito.Gnome.Tests/session.conf" -- \
    gnome-shell-test-tool --headless --disable-animations --extra-filter io.github.traciborski.Dito.Test \
    --extension "$archive" "$root/tests/Dito.Gnome.Tests/smoke.js"
