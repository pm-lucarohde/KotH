#!/bin/bash
# Two clients on this PC, one per monitor: host on the left, guest on the right.
#   Tools/two_clients.sh [aot|jit] [MAP]   (default: aot, Bin/Linux/aot; the host's map, else MountDestiny)
# Only the host is heard: the guest gets OpenAL's null driver, so nothing plays twice.
# Closing one window or Ctrl+C here ends both.
set -e
cd "$(dirname "$0")/.."
case ${1:-aot} in
  aot) dir=Bin/Linux/aot ;;
  jit) dir=Bin/Linux/Release; export DOTNET_EnableAVX=0 ;;
  *) echo "unknown: $1 (aot or jit)" >&2; exit 64 ;;
esac
cd "$dir"
./KotH --host --min-players 2 --display 0 --name Left ${2:+--map "$2"} &
host=$!
sleep 2
ALSOFT_DRIVERS=null ./KotH --join 127.0.0.1 --display 1 --name Right &
guest=$!
trap 'kill $host $guest 2>/dev/null' EXIT INT TERM
wait -n $host $guest || true
