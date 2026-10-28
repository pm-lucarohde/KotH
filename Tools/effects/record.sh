#!/bin/bash
# Records the effect permutations the game uses (KOTH_EFFECTLOG) on a headless, silent tour of
# the AOT client (menus, rounds with bots, blaster, Esc menu, banner, Test map), then merges them into
# KotH.Game/Assets/EffectCompileLog.sdeffectlog (EffectLog.cs).
#   Tools/effects/record.sh [BIN]   (default Bin/Linux/aot; then publish again)
set -e
cd "$(dirname "$0")/../.."
root=$PWD; bin=${1:-Bin/Linux/aot}; out=$root/.work/effects; mkdir -p $out
log=$out/record.bin; rm -f $log
run() {  # run "ENV=.. ENV=.." ARGS..
  local envs=$1; shift
  (cd $bin && env KOTH_HEADLESS=1 KOTH_MUSIC_VOLUME=0 KOTH_EFFECTLOG=$log KOTH_SHOT=$out/shot.png $envs \
     timeout 150 ./KotH "$@" > $out/run.log 2>&1) || echo "run $envs $*: error $?" >&2
  echo "$(grep -c '^\[effects\] [0-9]' $out/run.log) new: $envs $*"
}
run "KOTH_SHOT_MENU=1 KOTH_SHOT_SECONDS=4"
run "KOTH_SHOT_MENU=1 KOTH_SHOT_MENU_PAGE=0 KOTH_SHOT_SECONDS=10"
run "KOTH_SHOT_LOAD=1 KOTH_SHOT_SECONDS=2" --offline
run "KOTH_SHOT_SECONDS=14 KOTH_SHOT_STRIKE=2,4,6 KOTH_SHOT_HIT=0,0.5 KOTH_SHOT_MOVE=0,1,sprint KOTH_SHOT_CROUCH=8 KOTH_SHOT_JUMP=10,10.3" --offline --bots 3
run "KOTH_SHOT_SECONDS=14 KOTH_SHOT_THIRD=1 KOTH_SHOT_STRIKE=2,4 KOTH_SHOT_HIT=90 KOTH_SHOT_BLINK=1 KOTH_SHOT_TURN=180,6,90" --offline --bots 3
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_BLASTER=1 KOTH_SHOT_FIRE=3,4,5" --offline --bots 3
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1 KOTH_SHOT_BLASTER=1 KOTH_SHOT_FIRE=3,4,5" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_PAUSE=open" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_PAUSE=settings" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_HUD=banner" --offline --bots 3
run "KOTH_SHOT_SECONDS=10 KOTH_SHOT_STRIKE=2,4 KOTH_SHOT_HIT=0" --offline --bots 3 --map Test
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1" --offline --bots 3 --map Test
dotnet run Tools/effects/EffectLog.cs -- $log KotH.Game/Assets/EffectCompileLog.sdeffectlog
