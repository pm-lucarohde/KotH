#!/bin/bash
# learn_jumps.sh MAP [PROBES_DIR]: 104 climbs (8 processes x 13 seeds) add every judged jump to
# KotH.Shared/Resources/Maps/MAPJumps.txt (delete it to start afresh), then rebuild the probes/game.
MAP=${1:?map}; BIN=$(realpath "${2:-Bin/Probes/aot}"); ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=$(mktemp -d "$ROOT/.work/jumps.XXXX"); FILE="$ROOT/KotH.Shared/Resources/Maps/${MAP}Jumps.txt"
for i in $(seq 0 7); do
  (cd "$BIN"; DOTNET_EnableAVX=0 KOTH_CLIMB_MAP=$MAP KOTH_CLIMB_FIRST=$((1 + i*13)) KOTH_JUMPS_OUT="$OUT/part$i.txt" \
    taskset -c 0-7,9,11-19,21,23 timeout 900 ./KotH.Probes bots climb 13 > "$OUT/part$i.log" 2>&1) &
done
wait
# Sum by the two floors; jumps tried once only are left out.
cat "$OUT"/part*.txt $( [ -f "$FILE" ] && echo "$FILE" ) |
  awk '{k=$1" "$2" "$3" "$4" "$5" "$6; g[k]+=$7; m[k]+=$8} END {for (k in g) if (g[k]+m[k] >= 2) print k, g[k], m[k]}' |
  sort > "$OUT/merged.txt" && mv "$OUT/merged.txt" "$FILE"
echo "$(wc -l < "$FILE") jumps in $FILE; climbs in $OUT"
