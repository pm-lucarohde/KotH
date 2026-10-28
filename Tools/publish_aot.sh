#!/bin/bash
# NativeAOT builds of client, server and probes, linked in the distrobox "aot"
# (clang, lld, zlib-devel). Output: Bin/Linux/aot, Bin/Server/aot, Bin/Probes/aot.
# Building with the runtime identifier first is required: publish alone finds
# no assembly for the asset compiler. Never incremental: a new script field
# leaves the old scene bundle in place, and loading MainScene then crashes.
# ILC occasionally crashes; then run again. Windows only on request: linked with
# lld-link against the CRT and SDK libraries of "xwin splat" (Directory.Build.targets).
#   Tools/publish_aot.sh [linux|server|probes|windows|probes-windows ...]   (default: the first three)
set -e
cd "$(dirname "$0")/.."
targets=${*:-linux server probes}
for t in $targets; do
  rid=linux-x64; exe=
  case $t in
    linux)  proj=KotH.Linux;  out=Bin/Linux/aot ;;
    server) proj=KotH.Server; out=Bin/Server/aot ;;
    probes) proj=KotH.Probes; out=Bin/Probes/aot ;;
    windows) proj=KotH.Windows; out=Bin/Windows/aot; rid=win-x64; exe=.exe ;;
    probes-windows) proj=KotH.Probes; out=Bin/Probes/aot-win; rid=win-x64; exe=.exe ;;
    *) echo "unbekannt: $t" >&2; exit 64 ;;
  esac
  echo "== $proj -> $out"
  # The effect cache outlives the build: Stride compiles shaders on first use
  # and keys them by content, so a fresh build need not compile them all again.
  if [ -d "$out/cache" ]; then rm -rf "$out.cache"; mv "$out/cache" "$out.cache"; fi
  rm -rf "$out"
  distrobox enter aot -- bash -c "export DOTNET_ROOT=\$HOME/.dotnet PATH=\$HOME/.dotnet:\$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
    cd '$PWD' && dotnet build $proj -c Release -r $rid -p:PublishAot=true --no-incremental -v q -nologo \
      && dotnet publish $proj -c Release -r $rid -p:PublishAot=true -o $out -v q -nologo" \
    | grep -E 'error|warning (IL|AOT)|->' || true
  name=${proj/KotH.Linux/KotH}
  test -x "$out/${name/KotH.Windows/KotH}$exe" || { echo "$proj: kein Binary" >&2; exit 1; }
  # Stride bug: the effects of EffectCompileLog.sdeffectlog get compiled, but their index
  # entries stay in the first command's database, so the bundle lacks them. The game also
  # finds them without the index: a 16-byte object named by the input hash holds the
  # bytecode's id. Both go as loose objects into data/db.
  db=KotH.Game/obj/stride/assetbuild/data/db; effects=0
  if [ "$t" = linux ] || [ "$t" = windows ]; then
    for f in $(cd $db && find . -type f -size 16c -path "./??/*"); do
      id=$(od -An -tx1 -N16 "$db/$f" | tr -d ' \n'); code=${id:0:2}/${id:2}
      [ -f "$db/$code" ] || continue
      mkdir -p "$out/data/db/${f:2:2}" "$out/data/db/${id:0:2}"
      cp "$db/$f" "$out/data/db/$f"; cp "$db/$code" "$out/data/db/$code"; effects=$((effects + 1))
    done
    echo "   $effects vorübersetzte Effekte"
  fi
  if [ -d "$out.cache" ]; then mv "$out.cache" "$out/cache"; fi
done
