# CLAUDE.md

KotH: king-of-the-hill game on **Stride 4.4**, **.NET 11 RC2** (`global.json`), **Vulkan**, **NativeAOT**, Linux; from Unity
(Git up to `577ed3f`, reference in `Reference/unity/`). Peer-to-peer (one peer hosts). Derivations and history: the memory.

## Conventions

- Everything in the repo is **English** (code, logs, `Display`/`userdoc` texts, docs, commits). Replies to Luca in **German**.
- Code comments **at most 4 lines per block**, XML doc included (`.work/english/cblocks.py` checks).
- Docs short: README ≤ 50 lines, this file ≤ 100. Ask about architecture instead of assuming; no leftovers unasked.
- Logic as in Unity: no regression, bit-exact determinism. Do not touch `Bildschirmfoto_*.png`, `mono_crash.*`.
- End game processes by PID, never `pkill -f KotH…`. Git: push `origin main` after Luca's approval.

## Build and run

```bash
Tools/publish_aot.sh [linux|server|probes]   # NativeAOT -> Bin/{Linux,Server,Probes}/aot (Luca tests only AOT); windows on request
```

- AOT links in the distrobox `aot`; ILC sometimes crashes, then rerun; always `--no-incremental` (else an old scene
  bundle). After new materials/effects/scenes: `Tools/effects/record.sh`. JIT = AOT only with `DOTNET_EnableAVX=0`.
- Client: no arguments = main menu, `--offline` alone; `--host`/`--join ADDRESS[:PORT]`, `--name`, `--port`,
  `--min-players`, `--bots N`, `--mode duo|trio|squad`, `--display N`, `--map NAME`, `--fps N`, `--dlss MODE`. Two clients: `Tools/two_clients.sh`.
- Server: real time, `--seconds N` or `--ticks N` (fast, exact end state); `--map`, `--bots`, `--mode …`, load test `--bodies N`
  (`--map Test`); network `--host`/`--join`/`--bot` (`NetPlay.cs`). Traces `KOTH_TRACE=BotName`, `KOTH_FALLS=1`.
- Test aids (`KotHApp.cs` fields, times from the end of the load screen): `KOTH_SHOT=shot.png` (+ `_SECONDS`, `_YAW`,
  `_AT`, `_MOVE`, `_BLASTER`, `_FIRE`, `_VIDEO` …), `KOTH_HEADLESS=1`, `KOTH_SIZE`, `KOTH_MUSIC_VOLUME=0` (mute), `KOTH_EFFECTLOG`,
  `KOTH_POSLOG`; menu shots `KOTH_SHOT_MENU`, `_PAUSE`, `_HUD=banner`, `_CHAT=text`, `_TALK=1`, `_LOAD`, `_SETTINGS=tab`, `_APPLY=s:x.toml`; `KOTH_SETTINGS`;
  start log `~/.local/share/KotH/logs` (always), `KOTH_START_AWAY=focus|minimize` (`.work/startup/real.sh`); `KOTH_FRAMELOG=f`
  (+`_SECONDS`, `KOTH_VKTIMES=1`), `KOTH_GPULOG`, `KOTH_SHOT_PILOT=1` (bot pilot, `.work/perf/round.sh`); voice `KOTH_VOICE_LOOPBACK=m` (+`_HOLD=1`); still shots `KOTH_SHOT_STILL=1`, `KOTH_HIDE`, `KOTH_MAPCULL`.

## Tests: probes and regression

No unit tests; probes against the frozen Unity reference, exit 0 = passed (known fails: `netstate` since 5c95973, `rotation` ≤ 3 ULP).

```bash
Bin/Probes/aot/KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|slide|sprint|melee|blaster|dash|step|
  determinism|clock|stairrun|rampfoot|crouchedge|knockback|thirdperson|wallknock|capture|chain|netstate|netplay|bots>
Bin/Probes/aot/KotH.Probes determinism hashes   # hash chain 67AB05EEE3468AFD
Bin/Probes/aot/KotH.Probes bots climb 12        # MountDestiny, mean ≤ 140 s; also bots stats 12, bots jumplab
.work/tools/climb_many.sh DIR 8 13 && dotnet run .work/tools/climb_many.cs -- DIR   # 104 seeds, each bot
.work/tools/stride_runs.sh 3; R=Reference/unity/runs
dotnet run Tools/Verdict.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # verdicts (95, 94 the same)
dotnet run Tools/Regress.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # numbers within 5 mm / 1 %
```
- Deviations and intended changes: `Reference/REGRESSION.md`. Regression only if the measuring path is touched; bots
  ≥ 100 runs; runs ≤ 15 min. `.work/` holds tools and measurements (`/tmp` is volatile).
- Bot traces: `KOTH_BOTTRACE=tick` (`_BOT=n`), `KOTH_HOPLOG=n`, `KOTH_BOTPATH=file`, `KOTH_CLIMB_FIRST=k`, `KOTH_CLIMB_TEAM=2|3|4` (+`KOTH_CLIMB_SECONDS=1200`).

## Architecture

**KotH.Shared** (the whole simulation, no graphics), **KotH.Game** (client, `MainScene`, visible prefabs), **KotH.Linux**
(exe), **KotH.Server** (`HeadlessWorld`, 40 Hz), **KotH.Probes** (`Harness/`, `Ref` = Unity), **ThirdParty** (READMEs).

### Simulation
- `SimulationSystem` is the only clock: contacts → `Motor.Simulate` → `SeparateBodies` → `TieTeams` → `Publish` → `SyncHull` →
  combat → `HillMatch`. `Capture`/`Restore`/`Resimulate` (`WorldSnapshot`); no static state; client ticks in Draw (`SimulationJob`).
- `MotorState` holds **every** motor field (`Pack = 1`); `DeterminismProbe` checks bit-exactly with mutation tests.
- `TickClock` = Unity's player loop (`dt = 0.025f`); visuals interpolate, never feed back. Never read `TransformComponent`.
- `UMath` computes Unity's formulas operation by operation (never Stride's helpers); Sin/Cos/Atan2/Acos only via
  `DetMath` (`BannedSymbols.txt`). `WorldQueries`: Bepu with PhysX semantics; body hulls on layer 6 (`Hittable`).
- Jams: `PlayerMotor.EscapeJam` (up and aside, path clear by ray); never in probes, no state field.
- Coordinates: Stride −Z forward; Unity → Stride `z → −z`, yaw → −yaw, quaternion `(x,y,z,w) → (−x,−y,z,w)`.

### Network
- Rollback in a star: `HostSession` confirms ticks (inputs, joins, leaves), checksums every 40 ticks, state on desync;
  `PeerSession` (host too, `LocalTransport`) predicts, rolls back (ring 128, ≤ 15 ticks). `StateCodec` v4, `NetId` ≥ 4,
  same `BuildId` (MVID, instruction set, assets). `GameRoot` = `INetWorld`; wins only confirmed; never smooth corrections.
- Steam (app 480, `KotH.Game/Steam`; shots with `KOTH_STEAM=1`): the lobby's "host" hosts, a gone or slow one hands over
  (`HostChange.cs`, probe `migrate`); private rounds `PasswordKey` (probe `password`). Chat (T), push-to-talk voice
  (Opus, full ≤ 4 m, silent ≥ 30 m), Esc Players mutes: member to member (`SteamMesh`, `Comms`, `VoiceChat`).
  Else UDP (LiteNetLib 2.1.4, `--host`/`--join`, server), encrypted, key pinned by invite `koth://…&key=` (probe `secure`).

### Round (`KotH.Shared/Match`)
- `HillMatch`: 3 s alone takes the zone, holder scores inside; phases `Running`/`Over` (+ `Waiting`/`Countdown` net).
  Net `MatchStage`: lobby on MountDestiny, host picks the map (Esc), countdown end restarts there, round end back.
  Respawn below `RespawnBelow` (percent, points, shots stay), spawn ring round the zone (`MatchState.Seed`), lobby on markers.
- Teams (`MatchMode` duo/trio/squad): `Team` ids at round start (wishes: invite, `SteamParty`), most inside take, row chain.

### Maps and assets
- **Game Studio is the source of all assets** (YAML may be edited directly). `GraphicsCompositor` without `Archetype`.
- Per map `Maps/NAME`: `NAMECollision` (Shared: collision, markers, zone, bots; no models) and `NAMEVisual` (Game), root
  assets; `Hosting/Maps.cs` (`All` = host's choice). Map change in-process (`GameRoot.UnloadMap`); menu always MountDestiny.
- Probes load `Test`, except `bots climb`/`jumplab`. **MountDestiny** (123 m): `Tools/maps/Mountain.cs` → `MapUV`;
  textures `Textures.cs`, sky `Sky.cs`, clouds `Clouds.cs`; grass `GrassField`, shells `ShellField`. View ≤ 20 km.
- Game Studio under Proton (prefix `compatdata/2392464373`, memory); saves CRLF; its `.nuget` is a symlink, never replace files there.

### Client
- HUD `HudRenderer` (SpriteBatch) with Avalonia parts (`HillBoard`→`HudView`, `LoadView`) via `MenuOverlay`; main menu
  `KotH.Game/Menu` (map behind at 30 fps), Esc `PauseMenu`; `KotH.Game/Settings` (TOML, `QualityLevels`: High = assets).
- `MusicPlayer`, `SoundEffects` (sounds relative to the camera). Light: VXGI with a 4096² lightmap, static sun map,
  figure-only cascades (`FigureShadowMasks`); fog, clouds `AtmosphereEffects` before FXAA/DLSS; map clusters `MapCulling`.

### Bots (`KotH.Shared/Bots`) and figure
- **Only the host computes them, deterministically:** `BotDirector` decides tick t from the final state at t − 8;
  no bot state in the snapshot; in the client on its own thread. Read only snapshot states (teams: the lead's memory via `Peer`), no `System.Random`.
- `ClimbGrid` (0.5 m columns, floors, moves in seconds), RTAA* to the zone, `JumpPilot` flies every jump in the motor
  model first (no jitter, Luca), `BotBrain` utility AI with `ManeuverWfc`. Status: 312/312 on top, mean 90 s; duo 72/72 (≤ 470 s).
- Figure (visuals only): `Mushroom.glb` (17 clips, `.work/rig/build.sh`), FP arms in render group 2. The game moves
  `Mouth`, `Leaf.*`, lids, `Look`, `Recoil`, `*ArmSwing.R`: **clips must have no channels for them.** Cane/`Blaster` (1/2) on `Grip.R`.
