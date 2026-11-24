#!/bin/bash
# build.sh: Stride 4.4.1 (1be8907) with patches/koth.patch -> lib/ (Core, Graphics (Vulkan), Rendering, Engine,
# UI, Particles, BepuPhysics), which Directory.Build.targets puts in place of the packages'. Fetches the sources once
# (sparse, into src/), the few Linux LFS binaries the native build needs by hash, then builds in the box "aot".
set -e
cd "$(dirname "$0")"
TAG=1be8907
if [ ! -d src/.git ]; then
  git init -q src && git -C src remote add origin https://github.com/stride3d/stride.git
  git -C src config core.sparseCheckout true
  cp sparse-checkout src/.git/info/sparse-checkout
  git -C src fetch -q --depth 1 --filter=blob:none origin refs/tags/releases/4.4.1
  git -C src checkout -q FETCH_HEAD
fi
test "$(git -C src rev-parse --short=7 HEAD)" = "$TAG" || { echo "src is not at $TAG" >&2; exit 1; }
# Git LFS pointers of the Linux native libraries Stride.Audio links and the packages carry.
for f in deps/NativePath/dotnet/linux-x64/libCelt.a deps/NativePath/dotnet/linux-x64/libNativePath.a \
         deps/NativePath/dotnet/linux-x64/libCompilerRt.a deps/freetype/dotnet/linux-x64/libfreetype.so \
         deps/msdfgen/Release/linux-x64/stride_msdfgen.so; do
  oid=$(git -C src show HEAD:$f | sed -n 's/^oid sha256://p')
  if ! echo "$oid  src/$f" | sha256sum -c --quiet 2>/dev/null; then
    curl -sSfL -o "src/$f.tmp" "https://media.githubusercontent.com/media/stride3d/stride/$TAG/$f"
    echo "$oid  src/$f.tmp" | sha256sum -c --quiet && mv "src/$f.tmp" "src/$f"
  fi
done
# The patch applies to clean sources; already applied, it reverses cleanly instead.
if git -C src apply --check ../patches/koth.patch 2>/dev/null; then
  git -C src apply ../patches/koth.patch
else
  git -C src apply --check --reverse ../patches/koth.patch || { echo "koth.patch does not fit src" >&2; exit 1; }
fi
# One project at a time and no shared compiler: Stride's shader generator otherwise races or loads stale.
distrobox enter aot -- taskset -c 0-7,9,11-19,21,23 bash -c "export PATH=\$HOME/.dotnet:\$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
  cd '$PWD/src' && for p in Stride.UI Stride.Particles Stride.BepuPhysics; do
    d=sources/engine/\$p; test -d \$d/\$p && d=\$d/\$p
    dotnet build \$d/\$p.csproj -m:1 -c Release -p:StridePlatforms=Linux -p:StrideGraphicsApis=Vulkan \
      -p:UseSharedCompilation=false -v q -nologo | grep -E ' error |->' ; test \${PIPESTATUS[0]} -eq 0 || exit 1
  done"
mkdir -p lib
S=src/sources; R=bin/Release/net10.0
cp $S/core/Stride.Core/$R/Stride.Core.dll $S/engine/Stride.Graphics/$R/Vulkan/Stride.Graphics.dll \
   $S/engine/Stride.Rendering/$R/Stride.Rendering.dll $S/engine/Stride.Engine/$R/Stride.Engine.dll \
   $S/engine/Stride.UI/$R/Stride.UI.dll $S/engine/Stride.Particles/$R/Stride.Particles.dll \
   $S/engine/Stride.BepuPhysics/Stride.BepuPhysics/$R/Stride.BepuPhysics.dll lib/
echo "patched Stride -> $(pwd)/lib"
