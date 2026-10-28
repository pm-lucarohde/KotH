# CLAUDE.md

KotH: king-of-the-hill game on **Stride 4.4**, **.NET 11 RC2** (`global.json`), **Vulkan**, **NativeAOT**, Linux. Moved
from Unity (Git up to `577ed3f`); the measuring reference stays in `Reference/unity/`. Later peer-to-peer (one peer
hosts), no dedicated servers. Derivations and history live in the memory.

## Conventions

- Everything in the repo is **English** (code, logs, `Display`/`userdoc` texts, docs, commits). Replies to Luca in **German**.
- Code comments **at most 4 lines per block**, XML doc included (`.work/english/cblocks.py` checks).
- Docs short: README ≤ 50 lines, this file ≤ 100. Ask about architecture instead of assuming; no leftovers unasked.
- Logic as in Unity: no regression, bit-exact determinism. Do not touch `Bildschirmfoto_*.png`, `mono_crash.*`.
- End game processes by PID, never `pkill -f KotH…`. Git: push `origin main` after Luca's approval.

## Build and run

```bash
Tools/publish_aot.sh [linux|server|probes]   # NativeAOT -> Bin/{Linux,Server,Probes}/aot (Luca tests only AOT)
Tools/publish_aot.sh windows probes-windows  # on request only (lld-link, xwin)
```

- AOT links in the distrobox `aot`; ILC sometimes crashes, then rerun; always `--no-incremental` (else an old scene
  bundle). After new materials/effects: `Tools/effects/record.sh`. JIT matches AOT only with `DOTNET_EnableAVX=0`.
- Client: no arguments = main menu, `--offline` alone; `--host`/`--join ADDRESS[:PORT]`, `--name`, `--port`,
  `--min-players`, `--bots N`, `--display N`, `--map NAME`, `--fps N`. Two clients: `Tools/two_clients.sh` (Luca starts it).
- Server: real time, `--seconds N` or `--ticks N` (fast, exact end state); `--map`, `--bots`, load test `--bodies N`
  (`--map Test`); network `--host`/`--join`/`--bot` (`NetPlay.cs`). Traces `KOTH_TRACE=BotName`, `KOTH_FALLS=1`.
- Test aids (`KotHApp.cs` fields, times from the end of the load screen): `KOTH_SHOT=shot.png` (+ `_SECONDS`, `_YAW`,
  `_AT`, `_MOVE`, `_BLASTER`, `_FIRE`, `_VIDEO` …), `KOTH_HEADLESS=1`, `KOTH_SIZE`, `KOTH_MUSIC_VOLUME=0` (mute), `KOTH_EFFECTLOG`,
  `KOTH_POSLOG`; menu shots `KOTH_SHOT_MENU`, `_PAUSE`, `_HUD=banner`, `_LOAD`.

## Tests: probes and regression

No unit tests; probes against the frozen Unity reference, exit 0 = passed (`netstate` fails since 5c95973, known).

```bash
Bin/Probes/aot/KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|slide|sprint|melee|blaster|step|
  determinism|clock|stairrun|rampfoot|crouchedge|knockback|thirdperson|wallknock|capture|netstate|netplay|bots>
Bin/Probes/aot/KotH.Probes determinism hashes   # hash chain A044A364063D4C04
Bin/Probes/aot/KotH.Probes bots climb 12        # MountDestiny, mean ≤ 140 s; also bots stats 12, bots jumplab
.work/tools/climb_many.sh DIR 8 13 && dotnet run .work/tools/climb_many.cs -- DIR   # 104 seeds, each bot
.work/tools/stride_runs.sh 3; R=Reference/unity/runs
dotnet run Tools/Verdict.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # verdicts (95, 94 the same)
dotnet run Tools/Regress.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # numbers within 5 mm / 1 %
```

- Deviations and intended changes: `Reference/REGRESSION.md`. Regression only if the measuring path is touched; bots
  ≥ 100 runs; runs ≤ 15 min. `.work/` holds tools and measurements (`/tmp` is volatile).
- Bot traces: `KOTH_BOTTRACE=tick` (`_BOT=n`), `KOTH_HOPLOG=n`, `KOTH_BOTPATH=file`, `KOTH_CLIMB_FIRST=k`.

## Architecture

**KotH.Shared** (the whole simulation, no graphics), **KotH.Game** (client, `MainScene`, visible prefabs), **KotH.Linux**
(exe), **KotH.Server** (`HeadlessWorld`, 40 Hz), **KotH.Probes** (`Harness/`, `Ref` = Unity coordinates),
**ThirdParty/Stride.Voxels** (patched VXGI, own README).

### Simulation
- `SimulationSystem` is the only clock: contacts → `Motor.Simulate` → `SeparateBodies` → `Publish` → `SyncHull` →
  combat → `HillMatch`. `Capture`/`Restore`/`Resimulate` with `WorldSnapshot`; no static state.
- `MotorState` holds **every** motor field (`Pack = 1`); `DeterminismProbe` checks bit-exactly with mutation tests.
- `TickClock` = Unity's player loop (`dt = 0.025f`); visuals interpolate, never feed back. Never read `TransformComponent`.
- `UMath` computes Unity's formulas operation by operation (never Stride's helpers); Sin/Cos/Atan2/Acos only via
  `DetMath` (`BannedSymbols.txt`). `WorldQueries`: Bepu with PhysX semantics; body hulls on layer 6 (`Hittable`).
- Jams: `PlayerMotor.EscapeJam` (up and aside, path clear by ray); never in probes, no state field.
- Coordinates: Stride −Z forward; Unity → Stride `z → −z`, yaw → −yaw, quaternion `(x,y,z,w) → (−x,−y,z,w)`.

### Network
- Rollback in a star: `HostSession` confirms ticks (inputs, joins, leaves), checksums every 40 ticks, state on desync;
  `PeerSession` (host too, `LocalTransport`) predicts and rolls back (ring 128, ≤ 15 ticks ahead).
- `StateCodec` (version 4), bodies by `NetId` (players from 4), same `BuildId` needed (MVID, instruction set, assets).
- UDP via LiteNetLib 2.1.4, datagrams ≤ 1200 B, inputs clamped (`Sanitize`). Client: `GameRoot` is the `INetWorld`,
  `NetClient` holds the sessions; wins shown only confirmed; never smooth corrections.

### Round (`KotH.Shared/Match`)
- `HillMatch`: 3 s alone takes the zone, the holder scores while inside; phases `Running`/`Over`, networked also
  `Waiting`/`Countdown`. Respawn below `RespawnBelow`, percent, points and shots stay. Spawns random on a ring round the zone
  (seed from `MatchState.Seed`), lobby on markers. Probe `capture`.

### Maps and assets
- **Game Studio is the source of all assets** (YAML may be edited directly). `GraphicsCompositor` without `Archetype`.
- Per map `Maps/NAME`: `NAMECollision` (Shared: collision, markers, zone, bots; no models) and `NAMEVisual` (Game), both
  root assets; URLs in `Hosting/Maps.cs`. Probes load `Test`, except `bots climb`/`jumplab`.
- **MountDestiny** (123 m): `Tools/maps/Mountain.cs` → `Tools/maps/MapUV` → `publish_aot.sh`; textures `Textures.cs`,
  sky `Sky.cs`, clouds `Clouds.cs`; grass `GrassField`, shells `ShellField`. View distance ≤ 20 km.
- Game Studio runs under Proton (Steam shortcut, prefix `compatdata/2392464373`, setup in the memory); saving writes
  CRLF; the prefix `.nuget` is a symlink: never replace files there.

### Client
- HUD `HudRenderer` (SpriteBatch) with Avalonia parts (`HillBoard`→`HudView`, `LoadView`) via `MenuOverlay`; main menu
  `KotH.Game/Menu` (map behind at 30 fps, `SceneThrottle`), Esc `PauseMenu` (round runs on).
- `MusicPlayer`, `SoundEffects` (sounds relative to the camera). Light: VXGI with a 4096² lightmap, static sun map,
  cascades only for figures; fog and clouds in `AtmosphereEffects` before FXAA.

### Bots (`KotH.Shared/Bots`) and figure
- **Only the host computes them, deterministically:** `BotDirector` decides tick t from the final state at t − 8;
  no bot state in the snapshot; in the client on its own thread. Read only snapshot states, no `System.Random`.
- `ClimbGrid` (0.5 m columns, floors, moves in seconds), RTAA* to the zone, `JumpPilot` flies every jump in the motor
  model first (no jitter, Luca), `BotBrain` utility AI with `ManeuverWfc`. Status: 312/312 on top, mean 90 s.
- Figure (visuals only): `Mushroom.glb` (17 clips, `.work/rig/build.sh`), FP arms in render group 2. The game moves
  `Mouth`, `Leaf.*`, lids, `Look`, `Recoil`, `*ArmSwing.R`: **clips must have no channels for them.** Cane/`Blaster` (1/2) on `Grip.R`.
