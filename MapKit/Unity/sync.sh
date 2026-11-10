#!/usr/bin/env bash
# Copies the shared package code (KotH.MapFormat, without the Stride kit's unwrapper) into the
# Unity package, which cannot reach outside its folder. Run after every change to KotH.MapFormat.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
to="$here/com.koth.mapkit/Editor/MapFormat"
mkdir -p "$to"
for f in "$here"/../../KotH.MapFormat/*.cs; do
    cmp -s "$f" "$to/$(basename "$f")" || cp "$f" "$to/"
done
echo "MapFormat synced into $to"
