# CLAUDE.md

KotH: King-of-the-Hill-Spiel, umgezogen von Unity 6000.3 auf **Stride 4.4**, **.NET 11 RC2** (`global.json`),
**Vulkan**, **NativeAOT**, Linux. Unity ist entfernt (Git bis `577ed3f`); Messreferenz bleibt `Reference/unity/`.
Später Peer-to-Peer (ein Peer hostet), keine dedizierten Server. Herleitungen und Verlauf stehen in der Memory.

## Konventionen

- Antworten, Logs, `Display`-Texte, Tooltips (`/// <userdoc>`) **deutsch**; Klassen- und Objektnamen englisch.
- Code-Kommentare **englisch, höchstens 5 Zeilen pro Block** (XML-Doc zählt nicht).
- Bei Architektur- und Strukturfragen nachfragen statt annehmen. Restpunkte nicht ungefragt angehen.
- Logik wie in Unity: keine Regression, Determinismus bitgenau. `Bildschirmfoto_*.png`, `mono_crash.*` nicht anfassen.
- Spielprozesse per PID beenden, nie `pkill -f KotH…`. Git: Messages deutsch, `origin main` pushen nach Lucas Freigabe.

## Bauen und starten

```bash
dotnet build KotH.Linux -c Release     # ebenso KotH.Server, KotH.Probes -> Bin/{Linux,Server,Probes}/Release; dann ./KotH dort
Tools/publish_aot.sh [linux|server|probes]   # NativeAOT -> Bin/{Linux,Server,Probes}/aot
Tools/publish_aot.sh windows probes-windows  # nur auf Wunsch: Bin/Windows/aot, Bin/Probes/aot-win (lld-link, xwin)
```

- AOT linkt in der Distrobox `aot` (clang, lld, zlib-devel, libva); ILC stürzt manchmal ab, dann neu starten. Immer
  `--no-incremental` (sonst altes Szenen-Bundle, Client stürzt). Shader-Cache `aot/cache` überlebt den Build.
- Server: Echtzeit bis Strg+C, `--seconds N` oder `--ticks N` (so schnell wie möglich, µs/Tick, B/Tick,
  exakter Endzustand); `--map NAME` (Standard `Neu`); `--bots N --bot-seed N`; Lasttest `--bodies N --warmup N`
  (für `--map Test`, `BenchBot`s); `KOTH_TRACE=BotName`, `KOTH_FALLS=1`. Endzustands-Hash JIT = AOT.
  Netz: `--host [--port N] [--min-players N] [--bots N]`, `--join ADRESSE[:PORT]`, `--bot`, `--name` (`NetPlay.cs`).
- Client: ohne Argumente Hauptmenü, `--offline` gleich allein; `--host`/`--join`, `--name`, `--port`, `--min-players`, `--display N` (Monitor
  von links), `--map NAME` (Gäste nehmen die des Hosts), `--fps N` (Grenze, sonst frei). Zwei Clients: `Tools/two_clients.sh [aot|jit] [MAP]`
  (nicht selbst starten). JIT und AOT spielen zusammen, wenn JIT mit `DOTNET_EnableAVX=0` läuft (`BuildId`).
- Testhilfen (`KotHApp.cs`, Zeiten ab Ende des Ladebilds): `KOTH_SHOT=bild.png` (nach `_SECONDS`, F3 an), `_YAW`/`_PITCH`/`_FOV`,
  `_THIRD`, `_AT=x,y,z`, `_FACE=grad`, `_SWING`, `_SHAKE`, `_HIT=grad[,s]`, `_BLINK`, `_STRIKE=s,…`, `_MOVE=x,y[,sprint]`, `_CROUCH=s`,
  `_JUMP=s,…`, `_TURN=grad,s[,kreis]`, `_VIDEO=s` (30 fps nach `bild.png.rgba`, Format im Log); `KOTH_HEADLESS=1` (ohne Fenster),
  `KOTH_SIZE`, `KOTH_VSYNC`, `KOTH_FRAMELOG`, `KOTH_BOT_THREAD`, `KOTH_MUSIC_VOLUME` (stumm, auch Geräusche), `KOTH_MUSIC_TAIL`, `KOTH_SFX_VOLUME`, `KOTH_SOUNDLOG`, Ton `ALSOFT_DRIVERS=wave`. Kein Splash, 6 Render-Threads.

## Tests: Proben und Regression

Keine Unit-Tests; Proben gegen die eingefrorene Unity-Referenz. Exit-Code 0 = bestanden.

```bash
Bin/Probes/Release/KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|slide|sprint|melee|step|
  determinism|clock|stairrun|rampfoot|crouchedge|knockback|thirdperson|wallknock|capture|netstate|netplay|bots>
Bin/Probes/Release/KotH.Probes determinism hashes   # Hashkette C857D39208F53C83, JIT = AOT
Bin/Probes/Release/KotH.Probes bots climb 12        # Berg (Neu), Mittel ≤ 140 s, Zappeln/Blick; bots stats 12, jumplab
.work/tools/climb_many.sh ORDNER 8 13 && dotnet run .work/tools/climb_many.cs -- ORDNER   # 104 Seeds, jeder Bot einzeln
.work/tools/stride_runs.sh 3; R=Reference/unity/runs
dotnet run Tools/Verdict.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # Urteile (95, davon 94 gleich)
dotnet run Tools/Regress.cs -- $R/run1,$R/run2,$R/run3 .work/regress/run1   # Zahlen in 5 mm / 1 %
```

- Abweichungen und gewollte Änderungen in `Reference/REGRESSION.md`; ohne Unity-Referenz: `stairrun`, `rampfoot`,
  `crouchedge`, `thirdperson`, `wallknock`, `capture`, `netstate`, `netplay`. `.work/` hält Werkzeuge (`/tmp` flüchtig).
- **JIT nur mit `DOTNET_EnableAVX=0`** (AOT `x86-64-v2`, Luca). Regression nur, wenn der Messpfad berührt ist; Bots ≥ 100 Runs.
- Spuren: `KOTH_BOTTRACE=tick` (`_BOT=n`), `KOTH_HOPLOG=n`, `KOTH_BOTPATH=datei` (Draufsicht `.work/bots/routes.cs`),
  `KOTH_CLIMB_FIRST=k`, `KOTH_JAMTEST=1`; Labor `KOTH_LAB_LOOK=x,y,z`, `KOTH_LAB_STUCK=x,y,z`.

## Architektur

Projekte (`KotH.slnx`): **KotH.Shared** (ganze Simulation ohne Grafik, überall identisch), **KotH.Game**
(Client-Logik, `MainScene`, sichtbare Prefabs), **KotH.Linux** (Exe; `TrimmerRootAssembly` für Game und Shared),
**KotH.Server** (`HeadlessWorld`, 40 Hz), **KotH.Probes** (`Harness/`, `Ref` = Unity-Koordinaten), **ThirdParty/Stride.Voxels**.

### Simulation
- `SimulationSystem` ist der einzige Taktgeber. `Step()`: Kontakte → `Motor.Simulate` für alle (eigene Pose) →
  `SeparateBodies` → `Publish` → `SyncHull` → Kampf → `HillMatch`. `Capture`/`Restore`/`Resimulate` mit
  `WorldSnapshot`; kein statischer Zustand; `Restore` stellt auch Körperliste und Reihenfolge her.
- `MotorState` muss **jedes** Motorfeld enthalten (`Pack = 1`); `DeterminismProbe` prüft bitgenau mit Mutationstests.
- `TickClock` = Unitys Player-Loop (max. 0,333 s/Frame, `dt = 0.025f`); Darstellung interpoliert, wirkt nie zurück.
- `UMath` rechnet Unitys Formeln Operation für Operation; in der Simulation nie Strides Helfer (`Normalize` rundet
  anders). Sin/Cos/Atan2/Acos nur über `DetMath` (C-Laufzeit Linux ≠ Windows; `BannedSymbols.txt` sperrt `Math.Sin` & Co.).
  `WorldQueries`: Bepu mit PhysX-Semantik (Startüberlappung = Abstand 0, Strahl aus dem Inneren sieht nichts, stabile
  Sortierung); Körperhüllen sind Statics auf Layer 6 (`Hittable`). Nie aus `TransformComponent` lesen.
- Klemmen (Lucas Wahl): Kapsel steil ≥ 0,25 s in einem Schacht schmaler als sie, nicht am Boden, `wedged`, < 0,4 m/s
  → `PlayerMotor.EscapeJam`: 0,1–0,8 m hoch, 0,3/0,6 m seitlich, Tempo 0; nie in Regression/Proben, ohne Zustandsfeld.
- Koordinaten: Stride (−Z vorn); Unity → Stride `z → −z`, Gier → −Gier, Quaternion `(x,y,z,w) → (−x,−y,z,w)`.

### Netz
- Rollback im Stern (Lucas Entscheidung): `HostSession` bestätigt Ticks (Eingaben, Beitritte, Abgänge), vergleicht
  Prüfsummen alle 40 Ticks, schickt bei Desync den Zustand; `PeerSession` (auch der Host über `LocalTransport`)
  sagt fremde Eingaben vorher und rollt zurück (Ring 128, höchstens 15 Ticks voraus, dann steht die Uhr).
- `StateCodec` (Snapshot als Bytes, Version 3), Körper nach `NetId` (Spieler ab 4, Dummys 1–3 nur in Proben), in
  NetId-Reihenfolge angelegt. Mitspielen nur mit gleicher `BuildId` (MVID + Befehlssatz).
- UDP über LiteNetLib 2.1.4, Datagramme ≤ `NetProtocol.MaxDatagram` (1200 B); Host und Peer klemmen Eingaben
  (`Sanitize`). Client: `GameRoot` ist die `INetWorld`, `NetClient` hält die Sessions; HUD zeigt den Sieg nur bestätigt
  (`TryConfirmedMatch`); Korrekturen nie glätten. Gast `Ask` → `Map`, lädt, dann `Hello` (`Join`); Host öffnet nach dem Laden.

### Runde (`HillMatch`, `KotH.Shared/Match`)
- Zone: Einnehmen 3 s allein; danach bleibt sie dem Halter, solange er drin ist, er punktet auch mit anderen darin.
  Punkte in `BodyState`, Runde als `MatchState` im Snapshot. Nur mit `HeadlessWorld.StartMatch()`/`GameRoot`.
- Phasen `Running`/`Over`, im Mehrspieler zusätzlich `Waiting`/`Countdown` (`MinPlayers` > 0). Spieler ab
  `NetWorld.FirstPlayerId`. Respawn unter `RespawnBelow` (Mitte; Test −5 m, Neu 0 m), jede Phase; Prozent, Punkte bleiben.
- Startplätze (Lucas Wunsch): mit Ring an der `CaptureZone` (Neu 101–110 m, Abstand 6 m) zufällig rundum, Blick zur
  Zone, beim Rundenstart, Beitritt in eine laufende Runde und jedem Respawn in `Running`/`Over`; Lobby, Countdown und
  Test: Marker. 50 % neben einen Gesetzten (6–30 m). Zufall aus `MatchState.Seed` (Host würfelt), Tick, NetId.
  `SimulationBody.PlaceAt`; `PlacedTick`/`PlacedYaw` (kein Zustand) drehen den Blick im Client mit. Probe `capture`.

### Maps und Assets
- **Game Studio ist die Quelle aller Assets** (Lucas Entscheidung); die alten `Tools/generate_*.py` nicht
  wiederbeleben. `GraphicsCompositor` bewusst ohne `Archetype` (sonst gehen Tonemapping und Bloom an).
- Kollision und Körper in Prefabs **ohne Modelle** (`KotH.Shared/Assets/{Maps,Bodies}`), Client legt Modelle darüber.
- Je Map `Maps/NAME`: `NAMECollision` (Shared: Kollision, `PlayerSpawn0`–`11`, `DummySpawn`, `CaptureZone`, `Bots`)
  und `NAMEVisual` (Game), beide Root-Assets; URLs in `Hosting/Maps.cs`. `MainScene` ist nur der Rahmen. Host
  schickt den Namen im `Welcome` (`INetWorld.LoadMap`). Proben laden `Maps.Test`, außer `bots climb`/`jumplab`.
- **Neu** (Minecraft-Berg, 123 m): `Tools/maps/Mountain.cs` schreibt **nur** `Mountain.glb` (`COLOR_0` = Oberflächen,
  `TEXCOORD_0.x` = AO), `Sea.glb` und `NeuTerrain.bin` (Kollision, eingebettet, 4-m-Chunks, `WorldBuilder.AddTerrain`).
  Surface Nets 70 %, Säulen 2 × 1, ohne Wege, Gipfel aus ≥ 9/12 Richtungen; Stämme rund (kein Boden im Stamm), Kronen halb
  Blöcke, halb Büschel, mit Zweig-Karten. Danach `Tools/maps/MapUV` (Lightmap-UVs, Graskarte `NeuGrass.bin`), `publish_aot.sh`. Sichtweite ≤ 20 km.
- Gras: `GrassField` (Gruppe 5, Kacheln instanziert, Halme in `GrassBlade` gebaut, Licht der Lightmap); Muscheln `ShellField` (Gruppe 6). Texturen: `Tools/maps/Textures.cs` (comichaft),
  Shader in `KotH.Game/Effects`. Fallen: kein `Texture2D` als Generic (`LinkType` + `[Link]`), verschachteltes `ComputeColorParameterTexture`; Import merged.

### Game Studio (Wine/Proton)
- Steam-Verknüpfung „Stride Game Studio 4.4.0“, Prefix `compatdata/2392464373`, GE-Proton11-6, Windows-SDK .NET 11,
  Projekt `Z:\var\home\lr\KotH\KotH.slnx`; aus der Shell nur mit `env -u DOTNET_ROOT`. Speichern schreibt CRLF.
- Startoptionen `NUGET_PACKAGES=Z:/home/lr/.local/share/stride-winefix/packages MSBUILDDISABLENODEREUSE=1
  UseSharedCompilation=false %command%` (`Stride.Core` mit PR #3600). Prefix-`.nuget` = Symlink: nie Dateien ersetzen.

### Client: HUD, Kamera, Musik, Licht
- `HudRenderer` (SpriteBatch nach der Szene, `Numbers`, F3/Fadenkreuz direkt); Runde `HillBoard`→`HudView`, Ladebild `LoadView` (Uhr): Avalonia wie das Menü.
- Hauptmenü (`KotH.Game/Menu`, XAML, Avalonia 11.3 headless/Skia): `MenuOverlay` je Teil ein Offscreen-TopLevel mit festem Puffer, Upload nur bei neuem
  Bild, Einblenden in Stride; startet hinter dem Ladebild (wartet auf ruhige Bilder), `MenuCamera`, Map dahinter 30 fps (`SceneThrottle`), Menü frei; Erstellen = `--host`, Rest Optik; Shots: `KOTH_SHOT_MENU=1` (`_HOT`/`_PAGE=n`), `KOTH_SHOT_HUD=banner`, `KOTH_SHOT_LOAD=1`.
- `MusicPlayer` (`Music`): Menü, Lobby (bis `Running`), Spielbeginn, Playlist; 3,5 s Überblendung, 0,1224, im Netz bestätigt. `SoundEffects`: Ambient, Figuren (`PlayerVisualSystem.Sounded`) relativ zur Kamera, `Tools/audio/Sfx.cs`.
- Licht: VXGI (`ThirdParty/Stride.Voxels`, README), Profil 11; je Map `GI` (256 m, Bricks 0,125 m, GI 0,27, `LightVoxel.Sky`); Map-Licht
  und -Sonne (auch Meer; Blätterschatten stehen still) in Lightmap 4096², Map-Schatten einmal `StaticSunShadowRenderer`, Kaskaden nur Figuren (6 Kapseln).
  Nebel, Küstennebel (Ring über dem Wasser, Schwaden im Blau der Karte) und Wolken: `Atmosphere` (Neu) → `AtmosphereEffects` vor FXAA (halbe Auflösung), Karte `Tools/maps/Clouds.cs`, Schatten im `CascadeShadowReceiver`.

### Bots (`KotH.Shared/Bots`)
- Treten wie Spieler bei (NetId ab 4, Slots nach `NetWorld.BotSlots`), offline/Host `--bots N` (Standard 3), stehen bei Rundenbeginn 3 s (`StartWait`).
  **Nur der Host rechnet sie, deterministisch:** `BotDirector` berechnet Tick t einmal, in Reihenfolge, nur aus dem
  endgültigen Zustand bei t − 8 (`ReactionTicks`), Raster und xorshift; kein Bot-Zustand im Snapshot; im Client voraus auf
  eigenem Thread (`KOTH_BOT_THREAD=0`: je Frame 0,25 ms). Host bestätigt sie in `Finalise`, sein Peer kennt sie (`KnownInputs`).
- `ClimbGrid` (statt DotRecast): Spalten 0,5 m, bis 4 Böden mit 1,8 m Luft; Gehen, Fallen, Sprung, Doppelsprung,
  Lücke, Kosten in Sekunden; Sprünge und Abstiege brauchen einen Korridor so breit wie der Körper, `FloorAt` nimmt den
  Boden auf Fußhöhe. Zeitfeld per Dijkstra, Wegvarianten je Bot; zur Zone RTAA*, sonst A*. `Memory` (Strafen auf
  Fehlsprünge und ähnliche Züge, Aufschläge ohne Fortschritt) gehört dem Bot.
- `JumpPilot` fliegt jeden Sprung vorher im Motormodell, springt erst, wenn er landet und sich am Boden sieht; nach
  der Landung lässt er los, bis er sich sieht, gehaltene Tasten bleiben, keine Umkehr ohne Pause (Lucas Wunsch: kein
  Zappeln). Blick wie eine Hand an der Maus (Trägheit, leichte Unruhe, nickt zum Weg oder zur Stufe), Gehen peilt
  1,8 m voraus. `BotBrain`: Utility AI, `ManeuverWfc`; weit unter der Zone nur Schlag im Vorbeigehen, nur in `Running`.
- Nur Snapshot-Zustände lesen, nie lebende Komponenten (außer Einstellungen); Trigonometrie über `UMath`/`DetMath` in
  double, keine `System.Random`. Stand: 312/312 Bots oben, Mittel 90 s, höchstens 145 s.

### Figur und Animation (nur Darstellung)
- Pilzfigur `Mushroom.glb` mit 17 Clips; `PlayerVisualSystem` mischt sie nach Tempo und Richtung, Lauf-Clips auf
  gemeinsamer Phase aus der Strecke; geduckt fest auf 75 % Höhe, 90 % Breite. Gebaut mit `.work/rig/build.sh`
  (Blender: `rig.py`, `fp_arms.py`, `bamboo.py`). FP-Arme `MushroomArms`, Render-Gruppe 2, ohne Schatten, immer vorn (`ArmsInFront`).
- Das Spiel bewegt `Mouth`, `Leaf.*`, Lider, `Look`, `Recoil`, `UpperArmSwing.R`/`LowerArmSwing.R`: **Clips dürfen
  dafür keine Kanäle haben.** Auslöser `LastHitTick`, `LastHitAway`, `LastSwingTick`, `LookPitch` sind kein Zustand.
- Bambusstab an `Grip.R` (`GameRoot.AttachHeld`), Blatt federt (`Sway`); Schlag `ArmSwing` (Zwei-Knochen-IK); in
  Third Person dreht der Oberkörper bis 60° zum Ziel (`Twist`). Keine Umrisslinien.
