#!/usr/bin/env bash
set -euo pipefail

rid=${1:?Usage: bash scripts/package-gnome.sh linux-x64|linux-arm64 VERSION OUTPUT.zip}
version=${2:?Version required}
archive=${3:?Output ZIP path required}
case "$rid" in
    linux-x64|linux-arm64) ;;
    *) echo "Unsupported runtime: $rid" >&2; exit 1 ;;
esac

root=$(cd "$(dirname "$0")/.." && pwd -P)
mkdir -p "$(dirname "$archive")"
archive="$(cd "$(dirname "$archive")" && pwd)/$(basename "$archive")"
stage=$(mktemp -d)
trap 'rm -r -- "$stage"' EXIT

source_dir="$root/src/Dito.Gnome"
cp "$source_dir/extension.js" "$source_dir/clipboard.js" "$source_dir/historyClient.js" "$stage"
jq --arg version "$version" '. + {"version-name": $version}' "$source_dir/metadata.json" > "$stage/metadata.json"
dotnet publish "$source_dir/History" -c Release -r "$rid" --self-contained true \
    "-p:Version=$version" -o "$stage/history"
rm -f -- "$archive"
(cd "$stage" && zip -qr "$archive" .)
