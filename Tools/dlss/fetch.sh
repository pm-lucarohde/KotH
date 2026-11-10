#!/bin/bash
# NVIDIA's DLSS SDK files the client needs, fetched once into .work/dlss (not in the repo):
# the DLSS Super Resolution library shipped beside the game, the static NGX loader linked into it
# and the headers the binding follows. Pinned version, checked by SHA-256.
#   Tools/dlss/fetch.sh [linux] [windows]   (default: linux)
set -e
cd "$(dirname "$0")/../.."
version=v310.9.16
dir=.work/dlss/$version
base=https://raw.githubusercontent.com/NVIDIA/DLSS/$version
files="LICENSE.txt include/nvsdk_ngx.h include/nvsdk_ngx_defs.h include/nvsdk_ngx_params.h include/nvsdk_ngx_vk.h
  include/nvsdk_ngx_helpers.h include/nvsdk_ngx_helpers_vk.h include/nvsdk_ngx_defs_vk.h"
for os in ${*:-linux}; do
  case $os in
    linux) files="$files lib/Linux_x86_64/rel/libnvidia-ngx-dlss.so.310.9.16 lib/Linux_x86_64/libnvsdk_ngx.a" ;;
    windows) files="$files lib/Windows_x86_64/rel/nvngx_dlss.dll lib/Windows_x86_64/x64/nvsdk_ngx_s.lib" ;;
    *) echo "unknown: $os" >&2; exit 64 ;;
  esac
done
for f in $files; do
  [ -f "$dir/$f" ] && continue
  mkdir -p "$dir/$(dirname "$f")"
  curl -sfL "$base/$f" -o "$dir/$f.part" && mv "$dir/$f.part" "$dir/$f" || { echo "DLSS: $f not fetched" >&2; exit 1; }
done
for f in $files; do grep "  $f\$" Tools/dlss/SHA256SUMS; done | (cd "$dir" && sha256sum -c --quiet -)
