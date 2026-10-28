# CLAUDE.md

KotH: king-of-the-hill game, moved from Unity 6000.3 to **Stride 4.4**, **.NET 11 RC2** (`global.json`), **Vulkan**,
**NativeAOT**, Linux. Unity is gone (Git up to `577ed3f`); the measuring reference stays in `Reference/unity/`.
Later peer-to-peer (one peer hosts), no dedicated servers. Derivations and history live in the memory.

## Conventions

- Everything in the repo is **English**: code, comments, logs, `Display` texts, tooltips (`/// <userdoc>`), docs, commit
  messages. Replies to Luca in **German**.
- Code comments **at most 4 lines per block**, XML doc included (`.work/english/cblocks.py` checks).
- Ask about architecture and structure instead of assuming. Do not tackle leftovers unasked.
- Logic as in Unity: no regression, bit-exact determinism. Do not touch `Bildschirmfoto_*.png`, `mono_crash.*`.
- End game processes by PID, never `pkill -f KotH…`. Git: push `origin main` after Luca's approval.

## Build and run

```bash
dotnet build KotH.Linux -c Release     # likewise KotH.Server, KotH.Probes -> Bin/{Linux,Server,Probes}/Release; then ./KotH there
Tools/publish_aot.sh [linux|server|probes]   # NativeAOT -> Bin/{Linux,Server,Probes}/aot
Tools/publish_aot.sh windows probes-windows  # on request only: Bin/Windows/aot, Bin/Probes/aot-win (lld-link, xwin)
```

- AOT links in the distrobox `aot`; ILC sometimes crashes, then rerun. Always `--no-incremental` (else an old scene
  bundle). Shader cache `aot/cache` survives the build. After new materials/effects: `Tools/effects/record.sh`.
- Server: real time until Ctrl+C, `--seconds N` or `--ticks N` (as fast as possible, µs/tick, B/tick, exact end state);
  `--map NAME` (default `MountDestiny`); `--bots N --bot-seed N`; load test `--bodies N --warmup N` (for `--map Test`,
  `BenchBot`s); `KOTH_TRACE=BotName`, `KOTH_FALLS=1`. End-state hash JIT = AOT.
  Network: `--host [--port N] [--min-players N] [--bots N]`, `--join ADDRESS[:PORT]`, `--bot`, `--name` (`NetPlay.cs`).
- Client: no arguments = main menu, `--offline` straight alone; `--host`/`--join`, `--name`, `--port`, `--min-players`,
  `--display N` (monitor from the left), `--map NAME` (guests take the host's), `--fps N` (cap, else free). Two clients:
  `Tools/two_clients.sh [aot|jit] [MAP]` (do not start it yourself). JIT and AOT play together if JIT runs with
  `DOTNET_EnableAVX=0` (`BuildId`).
- Test aids (`KotHApp.cs` header, times from the end of the load screen): `KOTH_SHOT=shot.png` (+ `_SECONDS`, `_YAW`,
  `_AT`, `_MOVE`, `_VIDEO` …), `KOTH_HEADLESS=1`, `KOTH_SIZE`, `KOTH_MUSIC_VOLUME=0` (mute), `KOTH_EFFECTLOG`,
  `KOTH_POSLOG` (own position per tick, F8 marks). No splash, 6 render threads.

## Tests: probes and regression

No unit tests; probes against the frozen Unity reference. Exit code 0 = passed. Output uses decimal points; the Unity
reference logs were translated and converted to match.

```bash
Bin/Probes/Release/KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|slide|sprint|melee|step|
  determinism|clock|stairrun|rampfoot|crouchedge|knockback|thirdperson|wallknock|capture|netstate|netplay|bots>
Bin/Probes/Release/KotH.Probes determinism hashes   # hash chain C857D39208F53C83, JIT = AOT
Bin/Probes/Release/KotH.Probes bots climb 12        # mountain (MountDestiny), mean ≤ 140 s, jitter/view; bots stats 12, jumplab
.work/tools/climb_many.sh DIR 8 13 && dotnet run .work/tools/climb_many.cs -- DIR   # 104 seeds, every bot on its own
.work/tools/stride_runs.sh 3; R=Reference/unity/runs
dotnet run Tools/Verdict.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # verdicts (95, 94 of them the same)
dotnet run Tools/Regress.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # numbers within 5 mm / 1 %
```

- Deviations and intended changes in `Reference/REGRESSION.md`; without Unity reference: `stairrun`, `rampfoot`,
  `crouchedge`, `thirdperson`, `wallknock`, `capture`, `netstate`, `netplay`. `.work/` holds tools (`/tmp` is volatile).
- **JIT only with `DOTNET_EnableAVX=0`** (AOT `x86-64-v2`, Luca). Regression only if the measuring path is touched; bots ≥ 100 runs.
- Traces: `KOTH_BOTTRACE=tick` (`_BOT=n`), `KOTH_HOPLOG=n`, `KOTH_BOTPATH=file`, `KOTH_CLIMB_FIRST=k`, `KOTH_JAMTEST=1`.

## Architecture

Projects (`KotH.slnx`): **KotH.Shared** (the whole simulation without graphics, identical everywhere), **KotH.Game**
(client logic, `MainScene`, visible prefabs), **KotH.Linux** (exe; `TrimmerRootAssembly` for Game and Shared),
**KotH.Server** (`HeadlessWorld`, 40 Hz), **KotH.Probes** (`Harness/`, `Ref` = Unity coordinates), **ThirdParty/Stride.Voxels**.

### Simulation
- `SimulationSystem` is the only clock. `Step()`: contacts → `Motor.Simulate` for all (own pose) → `SeparateBodies` →
  `Publish` → `SyncHull` → combat → `HillMatch`. `Capture`/`Restore`/`Resimulate` with `WorldSnapshot`; no static
  state; `Restore` also restores the body list and its order.
- `MotorState` must hold **every** motor field (`Pack = 1`); `DeterminismProbe` checks bit-exactly with mutation tests.
- `TickClock` = Unity's player loop (max 0.333 s/frame, `dt = 0.025f`); visuals interpolate, never feed back.
- `UMath` computes Unity's formulas operation by operation; never Stride's helpers in the simulation (`Normalize` rounds
  differently). Sin/Cos/Atan2/Acos only via `DetMath` (C runtime Linux ≠ Windows; `BannedSymbols.txt` bans `Math.Sin` & co.).
  `WorldQueries`: Bepu with PhysX semantics (start overlap = distance 0, a ray from inside sees nothing, stable
  sorting); body hulls are statics on layer 6 (`Hittable`). Never read from `TransformComponent`.
- Jams (Luca's choice): capsule steep ≥ 0.25 s in a shaft narrower than itself, not grounded, `wedged`, < 0.4 m/s →
  `PlayerMotor.EscapeJam`: 0.1–0.8 m up, 0.3/0.6 m sideways, path clear (ray, else into the mountain), speed 0; never in
  regression/probes, no state field.
- Coordinates: Stride (−Z forward); Unity → Stride `z → −z`, yaw → −yaw, quaternion `(x,y,z,w) → (−x,−y,z,w)`.

### Network
- Rollback in a star (Luca's decision): `HostSession` confirms ticks (inputs, joins, leaves), compares checksums every
  40 ticks, sends the state on a desync; `PeerSession` (the host too, via `LocalTransport`) predicts foreign inputs and
  rolls back (ring 128, at most 15 ticks ahead, then the clock stops).
- `StateCodec` (snapshot as bytes, version 3), bodies by `NetId` (players from 4, dummies 1–3 in probes only), created in
  NetId order. Playing together needs the same `BuildId` (MVID + instruction set + hash of the Shared assets).
- UDP via LiteNetLib 2.1.4, datagrams ≤ `NetProtocol.MaxDatagram` (1200 B); host and peer clamp inputs (`Sanitize`).
  Client: `GameRoot` is the `INetWorld`, `NetClient` holds the sessions; the HUD shows a win only when confirmed
  (`TryConfirmedMatch`); never smooth corrections. Guest `Ask` → `Map`, loads, then `Hello` (`Join`); host opens after loading.

### Round (`HillMatch`, `KotH.Shared/Match`)
- Zone: capture takes 3 s alone; then it stays with the holder while he is inside, he scores with others inside too.
  Points in `BodyState`, the round as `MatchState` in the snapshot. Only with `HeadlessWorld.StartMatch()`/`GameRoot`.
- Phases `Running`/`Over`, in multiplayer also `Waiting`/`Countdown` (`MinPlayers` > 0). Players from
  `NetWorld.FirstPlayerId`. Respawn below `RespawnBelow` (centre; Test −5 m, MountDestiny 0 m), any phase; percent and points stay.
- Spawns (Luca's wish): random on a ring around the `CaptureZone` (MountDestiny 101–110 m), facing it, in
  `Running`/`Over`; lobby, countdown and Test use markers. Seed from `MatchState.Seed`, tick, NetId. Probe `capture`.

### Maps and assets
- **Game Studio is the source of all assets** (Luca's decision); do not revive the old `Tools/generate_*.py`.
  `GraphicsCompositor` deliberately without `Archetype` (else tonemapping and bloom switch on).
- Collision and bodies in prefabs **without models** (`KotH.Shared/Assets/{Maps,Bodies}`), the client lays models on top.
- Per map `Maps/NAME`: `NAMECollision` (Shared: collision, `PlayerSpawn0`–`11`, `DummySpawn`, `CaptureZone`, `Bots`) and
  `NAMEVisual` (Game), both root assets; URLs in `Hosting/Maps.cs`. `MainScene` is only the frame. The host sends the
  name in `Welcome` (`INetWorld.LoadMap`). Probes load `Maps.Test`, except `bots climb`/`jumplab`.
- **MountDestiny** ("Mount Destiny", block mountain, 123 m): `Tools/maps/Mountain.cs` writes `Mountain.glb`, `Sea.glb`
  and `MountDestinyTerrain.bin` (embedded collision, `WorldBuilder.AddTerrain`); then `Tools/maps/MapUV` (lightmap UVs,
  `MountDestinyGrass.bin`), `publish_aot.sh`. Details in the memory. View distance ≤ 20 km.
- Grass: `GrassField` (group 5, instanced tiles, blades built in `GrassBlade`, lightmap light); shells `ShellField`
  (group 6). Textures: `Tools/maps/Textures.cs` (comic style, BC-compressed), sky `Tools/maps/Sky.cs` (sun = MainScene
  light), shaders in `KotH.Game/Effects`. Traps: no `Texture2D` as a generic (`LinkType` + `[Link]`), nested
  `ComputeColorParameterTexture`; the importer merges.

### Game Studio (Wine/Proton)
- Steam shortcut "Stride Game Studio 4.4.0", prefix `compatdata/2392464373`, GE-Proton11-6; launch options and the
  `Stride.Core` fix (PR #3600) in the memory. Saving writes CRLF. Prefix `.nuget` = symlink: never replace files there.

### Client: HUD, camera, music, light
- `HudRenderer` (SpriteBatch after the scene, `Numbers`, F3/crosshair direct); round `HillBoard`→`HudView`, load screen
  `LoadView` (clock): Avalonia like the menu.
- Main menu (`KotH.Game/Menu`, XAML, Avalonia 11.3 headless/Skia): `MenuOverlay`, per part an offscreen TopLevel with a
  fixed buffer, upload only on a new image, blended into Stride; starts behind the load screen (waits for calm frames),
  `MenuCamera`, map behind it at 30 fps (`SceneThrottle`), menu free; Host = `--host`, the rest is looks only; Esc in a
  game: `PauseMenu` (the round runs on; Main menu takes it down in `GameRoot.ReturnToMenu`, the map stays); shots:
  `KOTH_SHOT_MENU=1` (`_HOT`/`_PAGE=n`), `KOTH_SHOT_PAUSE=open|settings|menu`, `KOTH_SHOT_HUD=banner`, `KOTH_SHOT_LOAD=1`.
- `MusicPlayer` (`Music`): menu, lobby (until `Running`), round start, playlist; 3.5 s crossfade, 0.1224, confirmed in
  the network. `SoundEffects`: ambience, figures (`PlayerVisualSystem.Sounded`) relative to the camera, `Tools/audio/Sfx.cs`.
- Light: VXGI (`ThirdParty/Stride.Voxels`, README); map light and sun in a 4096² lightmap, map shadow once in
  `StaticSunShadowRenderer`, cascades only for figures. Fog, coastal fog, clouds and sun shafts: `Atmosphere` →
  `AtmosphereEffects` before FXAA (half resolution), cloud map `Tools/maps/Clouds.cs`.

### Bots (`KotH.Shared/Bots`)
- Join like players (NetId from 4), `--bots N` (default 3), stand 3 s at round start. **Only the host computes them,
  deterministically:** `BotDirector` computes tick t once from the final state at t − 8 (`ReactionTicks`); no bot state
  in the snapshot; in the client on its own thread. The host confirms them in `Finalise` (`KnownInputs`).
- `ClimbGrid` (instead of DotRecast): 0.5 m columns, up to 4 floors with 1.8 m of air; walk, drop, jump, double jump,
  gap, costs in seconds; jumps and descents need a corridor as wide as the body, `FloorAt` takes the floor at foot
  height. Time field by Dijkstra, route variants per bot; to the zone RTAA*, else A*. `Memory` (penalties on missed
  jumps and similar moves, landings without progress) belongs to the bot.
- `JumpPilot` flies every jump in the motor model first, jumps only when it lands; no reversal without a pause
  (Luca's wish: no jitter); view like a hand on the mouse. `BotBrain`: utility AI, `ManeuverWfc`.
- Read only snapshot states, never live components (except settings); trigonometry via `UMath`/`DetMath` in double,
  no `System.Random`. Status: 312/312 bots on top, mean 90 s, at most 145 s.

### Figure and animation (visuals only)
- Mushroom figure `Mushroom.glb` with 17 clips, blended by `PlayerVisualSystem`; built with `.work/rig/build.sh`
  (Blender). FP arms `MushroomArms`, render group 2, no shadow, always in front (`ArmsInFront`).
- The game moves `Mouth`, `Leaf.*`, lids, `Look`, `Recoil`, `UpperArmSwing.R`/`LowerArmSwing.R`: **clips must have no
  channels for them.** Triggers `LastHitTick`, `LastHitAway`, `LastSwingTick`, `LookPitch` are not state.
- Bamboo cane on `Grip.R` (`GameRoot.AttachHeld`), its leaf springs (`Sway`); swing `ArmSwing` (two-bone IK); in third
  person the torso turns up to 60° towards the target (`Twist`). No outlines.
