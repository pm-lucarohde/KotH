#!/bin/bash
# Builds the kit (Linux, Vulkan) and exports its default scene into KotH's maps folder; the window
# shows the result, then closes. --show keeps it open (fly the camera, F5 exports again).
#   MapKit/Stride/export.sh [--show] [--out DIR] [--scene URL]
set -e
cd "$(dirname "$0")"
dotnet build KotHMapKit.Linux -c Release -nologo -v q
once=--export
if [ "$1" = --show ]; then once=; shift; fi
exec Bin/Linux/Release/KotHMapKit $once "$@"
