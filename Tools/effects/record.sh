#!/bin/bash
# Records the effect permutations the game uses (KOTH_EFFECTLOG) on a headless, silent tour of
# the AOT client (menus, Customize, rounds with bots, duo chains, blaster, dash, Esc menu, banner, Test
# map, Atrium, The Ladder, Pyramid's pond (wading rings) and far forest, Volcano's crater, the sample custom map), then merges them into KotH.Game/Assets/EffectCompileLog.sdeffectlog (EffectLog.cs).
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
run "KOTH_SHOT_MENU=1 KOTH_SHOT_MENU_PAGE=3 KOTH_XP=3000 KOTH_SHOT_LOOK=2E5BD8,8A5A3C,D8B020,E0E0E0,FF3080 KOTH_SHOT_SECONDS=4"
run "KOTH_SHOT_MENU=1 KOTH_SHOT_MENU_PAGE=0 KOTH_SHOT_SECONDS=10"
run "KOTH_SHOT_LOAD=1 KOTH_SHOT_SECONDS=2" --offline
run "KOTH_SHOT_SECONDS=14 KOTH_SHOT_STRIKE=2,4,6 KOTH_SHOT_HIT=0,0.5 KOTH_SHOT_MOVE=0,1,sprint KOTH_SHOT_CROUCH=8 KOTH_SHOT_JUMP=10,10.3" --offline --bots 3
run "KOTH_SHOT_SECONDS=14 KOTH_SHOT_THIRD=1 KOTH_SHOT_STRIKE=2,4 KOTH_SHOT_HIT=90 KOTH_SHOT_BLINK=1 KOTH_SHOT_TURN=180,6,90" --offline --bots 3
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_BLASTER=1 KOTH_SHOT_FIRE=3,4,5" --offline --bots 3
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1 KOTH_SHOT_BLASTER=1 KOTH_SHOT_FIRE=3,4,5" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_DASH=2.6,3" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_THIRD=1 KOTH_SHOT_DASH=2.6,3" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_PAUSE=open" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_PAUSE=settings" --offline --bots 3
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_HUD=banner" --offline --bots 3
run "KOTH_SHOT_SECONDS=10 KOTH_SHOT_STRIKE=2,4 KOTH_SHOT_HIT=0" --offline --bots 3 --map Test
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1" --offline --bots 3 --map Test
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1" --offline --bots 3 --mode duo
run "KOTH_SHOT_SECONDS=10 KOTH_SHOT_THIRD=1 KOTH_SHOT_STRIKE=2,4 KOTH_SHOT_AT=0,0.95,27.5 KOTH_SHOT_FACE=0 KOTH_SHOT_PITCH=-20" --offline --bots 3 --map Atrium
# The Ladder: its tower from the spawn ring (tinted marble, slime, glass).
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1 KOTH_SHOT_AT=0,0.95,13 KOTH_SHOT_FACE=0 KOTH_SHOT_PITCH=30" --offline --bots 3 --map TheLadder
run "KOTH_SHOT_SECONDS=3 KOTH_SHOT_AT=-33,0.95,-31 KOTH_SHOT_FACE=0 KOTH_SHOT_YAW=-135 KOTH_SHOT_THIRD=4 KOTH_SHOT_PITCH=-35 KOTH_SHOT_MOVE=0,1" --offline --bots 3 --map Pyramid
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_AT=0,11.5,3 KOTH_SHOT_FACE=0 KOTH_SHOT_YAW=200 KOTH_SHOT_PITCH=-8" --offline --map Pyramid
# Volcano: the crater from its rim (magma, the wall's heat).
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_AT=0,137.2,10.4 KOTH_SHOT_FACE=0 KOTH_SHOT_YAW=0 KOTH_SHOT_PITCH=-60" --offline --bots 3 --map Volcano
# Custom maps: the sample package (Surface, Terrain, Leaves, Water templates; baked, live and no shadow).
sample=$root/KotH.Probes/Resources/MapPackages/Sample
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1" --offline --bots 3 --map-file $sample
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_YAW=-4" --offline --bots 3 --map-file $sample
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1" --offline --bots 3 --map-file $sample --dlss quality
# DLSS (needs an RTX card): every material once more with the object velocity output.
run "KOTH_SHOT_MENU=1 KOTH_SHOT_SECONDS=4 KOTH_DLSS=quality"
run "KOTH_SHOT_SECONDS=10 KOTH_SHOT_THIRD=1 KOTH_SHOT_STRIKE=2,4 KOTH_SHOT_BLASTER=5 KOTH_SHOT_FIRE=6,7 KOTH_SHOT_DASH=8.6,9" --offline --bots 3 --dlss quality
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_STRIKE=2,4" --offline --bots 3 --dlss quality
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1 KOTH_SHOT_AT=0,0.95,27.5 KOTH_SHOT_FACE=0 KOTH_SHOT_PITCH=-20" --offline --bots 3 --map Atrium --dlss quality
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1 KOTH_SHOT_AT=0,0.95,13 KOTH_SHOT_FACE=0 KOTH_SHOT_PITCH=30" --offline --bots 3 --map TheLadder --dlss quality
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_AT=0,137.2,10.4 KOTH_SHOT_FACE=0 KOTH_SHOT_YAW=0 KOTH_SHOT_PITCH=-60" --offline --bots 3 --map Volcano --dlss quality
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_AT=-33,0.95,-31 KOTH_SHOT_FACE=0 KOTH_SHOT_YAW=-135 KOTH_SHOT_THIRD=4 KOTH_SHOT_PITCH=-35 KOTH_SHOT_MOVE=0,1" --offline --bots 3 --map Pyramid --dlss quality
run "KOTH_SHOT_SECONDS=8 KOTH_SHOT_THIRD=1" --offline --bots 3 --map Test --mode duo --dlss quality
run "KOTH_SHOT_SECONDS=6 KOTH_SHOT_DASH=2.6,3" --offline --bots 3 --map Test
# The graphics levels (Settings page): Low (no FXAA), Medium and Ultra change shaders (GI steps).
for q in low medium ultra; do
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_MENU=1 KOTH_SHOT_SECONDS=4"
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_SECONDS=10 KOTH_SHOT_THIRD=1 KOTH_SHOT_STRIKE=2,4 KOTH_SHOT_BLASTER=5 KOTH_SHOT_FIRE=6,7 KOTH_SHOT_DASH=8.6,9" --offline --bots 3
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_SECONDS=6" --offline --bots 3 --map Test --mode duo
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_SECONDS=6" --offline --bots 3 --map-file $sample
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_SECONDS=8 KOTH_SHOT_AT=0,0.95,27.5 KOTH_SHOT_FACE=0 KOTH_SHOT_PITCH=-20" --offline --bots 3 --map Atrium
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_SECONDS=8 KOTH_SHOT_AT=0,0.95,13 KOTH_SHOT_FACE=0 KOTH_SHOT_PITCH=30" --offline --bots 3 --map TheLadder
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_SECONDS=8 KOTH_SHOT_AT=0,137.2,10.4 KOTH_SHOT_FACE=0 KOTH_SHOT_YAW=0 KOTH_SHOT_PITCH=-60" --offline --bots 3 --map Volcano
  run "KOTH_SETTINGS=$root/Tools/effects/$q.toml KOTH_SHOT_SECONDS=6 KOTH_SHOT_AT=-33,0.95,-31 KOTH_SHOT_FACE=0 KOTH_SHOT_YAW=-135 KOTH_SHOT_THIRD=4 KOTH_SHOT_PITCH=-35 KOTH_SHOT_MOVE=0,1" --offline --bots 3 --map Pyramid
done
dotnet run Tools/effects/EffectLog.cs -- $log KotH.Game/Assets/EffectCompileLog.sdeffectlog
