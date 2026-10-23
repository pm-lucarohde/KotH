#!/bin/bash
# NativeAOT builds of client, server and probes, linked in the distrobox "aot"
# (clang, lld, zlib-devel). Output: Bin/Linux/aot, Bin/Server/aot, Bin/Probes/aot.
# Building with the runtime identifier first is required: publish alone finds
# no assembly for the asset compiler. Never incremental: a new script field
# leaves the old scene bundle in place, and loading MainScene then crashes.
# ILC occasionally crashes; then run again.
#   Tools/publish_aot.sh [linux|server|probes ...]   (default: all three)
set -e
cd "$(dirname "$0")/.."
targets=${*:-linux server probes}
for t in $targets; do
  case $t in
    linux)  proj=KotH.Linux;  out=Bin/Linux/aot ;;
    server) proj=KotH.Server; out=Bin/Server/aot ;;
    probes) proj=KotH.Probes; out=Bin/Probes/aot ;;
    *) echo "unbekannt: $t" >&2; exit 64 ;;
  esac
  echo "== $proj -> $out"
  # The effect cache outlives the build: Stride compiles shaders on first use
  # and keys them by content, so a fresh build need not compile them all again.
  if [ -d "$out/cache" ]; then rm -rf "$out.cache"; mv "$out/cache" "$out.cache"; fi
  rm -rf "$out"
  distrobox enter aot -- bash -c "export DOTNET_ROOT=\$HOME/.dotnet PATH=\$HOME/.dotnet:\$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
    cd '$PWD' && dotnet build $proj -c Release -r linux-x64 -p:PublishAot=true --no-incremental -v q -nologo \
      && dotnet publish $proj -c Release -r linux-x64 -p:PublishAot=true -o $out -v q -nologo" \
    | grep -E 'error|warning (IL|AOT)|->' || true
  test -x "$out/${proj/KotH.Linux/KotH}" || { echo "$proj: kein Binary" >&2; exit 1; }
  if [ -d "$out.cache" ]; then mv "$out.cache" "$out/cache"; fi
done
