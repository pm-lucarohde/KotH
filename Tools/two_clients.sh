#!/bin/bash
# Two clients on this PC, one per monitor: host on the left, guest on the right.
#   Tools/two_clients.sh [aot|jit]   (default: aot, Bin/Linux/aot)
# Only the host is heard: the guest gets OpenAL's null driver, so nothing plays twice.
# Closing one window or Ctrl+C here ends both.
set -e
cd "$(dirname "$0")/.."
case ${1:-aot} in
  aot) dir=Bin/Linux/aot ;;
  jit) dir=Bin/Linux/Release; export DOTNET_EnableAVX=0 ;;
  *) echo "unbekannt: $1 (aot oder jit)" >&2; exit 64 ;;
esac
cd "$dir"
./KotH --host --min-players 2 --display 0 --name Links &
host=$!
sleep 2
ALSOFT_DRIVERS=null ./KotH --join 127.0.0.1 --display 1 --name Rechts &
guest=$!
trap 'kill $host $guest 2>/dev/null' EXIT INT TERM
wait -n $host $guest || true
