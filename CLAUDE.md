# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

KotH ist ein King-of-the-Hill-Spiel, umgezogen von Unity 6000.3 auf **Stride 4.4**,
**.NET 11 RC2** (`global.json` pinnt das SDK), **Vulkan** und **NativeAOT** unter Linux.
Das Unity-Projekt ist entfernt (letzter Stand in der Git-Geschichte: `577ed3f`); als
Messreferenz bleibt `Reference/unity/` mit eingefrorenen Läufen und Dumps.
Später soll Peer-to-Peer kommen (ein Peer hostet), keine dedizierten Server.

## Konventionen

- Antworten und Logs, `Display`-Texte, Tooltips (`/// <userdoc>`) auf **Deutsch**;
  Klassen- und Objektnamen englisch.
- Code-Kommentare **englisch, höchstens 5 Zeilen pro Block** (XML-Doc zählt nicht).
- Bei Architektur- und Strukturfragen nachfragen statt annehmen.
- Die Logik muss sich verhalten wie in Unity: keine Regression, Determinismus bitgenau.
- Capture-Zone und Restpunkte nicht ungefragt angehen. `Bildschirmfoto_*.png` und
  `mono_crash.*` im Wurzelordner nicht anfassen.
- Laufende Spielprozesse per PID beenden, nie `pkill -f KotH…` (trifft die eigene Shell).

## Bauen und starten

```bash
dotnet build KotH.Linux -c Release     # Client (JIT) -> Bin/Linux/Release/KotH
dotnet build KotH.Server -c Release    # Server       -> Bin/Server/Release/KotH.Server
dotnet build KotH.Probes -c Release    # Proben       -> Bin/Probes/Release/KotH.Probes
cd Bin/Linux/Release && ./KotH         # aus dem Ausgabeordner starten

Tools/publish_aot.sh [linux|server|probes]   # NativeAOT -> Bin/{Linux,Server,Probes}/aot
```

- AOT linkt in der Distrobox `aot` (clang, lld, zlib-devel); ILC stürzt gelegentlich
  ab, dann einfach erneut starten.
- Server: ohne Argument Echtzeit bis Strg+C, `--seconds N`, oder `--ticks N`
  (so schnell wie möglich, gibt µs/Tick, B/Tick und den exakten Endzustand aus).
  Lasttest: `--bodies N --warmup N` mit `BenchBot`s (geseedeter Zufall), Endzustands-Hash
  muss unter JIT und AOT gleich sein; `KOTH_TRACE=BotName`, `KOTH_FALLS=1`.
- Client-Testhilfen in `KotH.Linux/KotHApp.cs`: `KOTH_SHOT` (Screenshot, F3-Anzeige an),
  `KOTH_SHOT_YAW`/`_PITCH`/`_FOV`/`_SECONDS` (Standard 3 s), `KOTH_SHOT_THIRD=1`,
  `KOTH_SHOT_AT=x,y,z`, `KOTH_SHOT_SWING=1` (ein Schlag, meldet Treffer), `KOTH_SIZE=WxH`,
  `KOTH_VSYNC=0|1`, `KOTH_FRAMELOG=datei`.
- Client: kein Splash (asynchrones Laden + `WaitIdle` ohne QueueLock gab
  `VK_ERROR_DEVICE_LOST`), Renderer-Parallelität 6 (gemessen, Lucas Wahl).

## Tests: Proben und Regression

Es gibt keine Unit-Tests; geprüft wird mit **Proben** gegen die eingefrorene Unity-Referenz.

```bash
Bin/Probes/Release/KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|slide|sprint|melee|step|determinism|clock|stairrun|rampfoot|crouchedge|knockback|thirdperson|wallknock>
Bin/Probes/Release/KotH.Probes determinism hashes   # Hashkette + Befehlssatz

.work/tools/stride_runs.sh 3                        # alle Bewegungsproben 3x -> .work/regress/runK
R=Reference/unity/runs
python3 Tools/verdict.py $R/run1,$R/run2,$R/run3 .work/regress/run1   # Urteile gleich?
python3 Tools/regress.py $R/run1,$R/run2,$R/run3 .work/regress/run1   # Zahlen in 5 mm / 1 %?
```

- Exit-Code 0 heißt bestanden. Ziel: alle Urteile gleich (94/94), bekannte
  Abweichungen mit Ursache stehen in `Reference/REGRESSION.md`, ebenso die gewollten
  Änderungen gegenüber Unity (deren Probenfälle haben keine Unity-Referenz,
  `stairrun`, `rampfoot`, `crouchedge`, `thirdperson` und `wallknock` laufen
  außerhalb von `stride_runs.sh`).
- **JIT-Läufe brauchen `DOTNET_EnableAVX=0`**, sonst weichen die Floats von den
  AOT-Builds ab (`IlcInstructionSet=x86-64-v2` in `Directory.Build.props`, Lucas
  Entscheidung). Die Hashkette muss unter AOT und JIT gleich sein.
- Regression nur dann messen, wenn eine Änderung den Messpfad berührt.
- `.work/` (nicht versioniert) hält Werkzeuge und Messreihen; `/tmp` ist flüchtig.

## Architektur

Fünf Projekte (`KotH.slnx`):

- **KotH.Shared** – die gesamte Simulation, ohne Grafik. Läuft im Client, im Server
  und in den Proben identisch.
- **KotH.Game** – Client-Logik: `SimulationDriver`, Eingabe, Kamera, Darstellung, HUD,
  dazu `MainScene.sdscene` und die sichtbaren Prefabs.
- **KotH.Linux** – Exe des Clients (Vulkan, AOT); `TrimmerRootAssembly` für Game und
  Shared, weil Skripte nur über die Asset-Datenbank erreichbar sind.
- **KotH.Server** – Konsole: Arena + Dummy über `HeadlessWorld`, 40 Hz.
- **KotH.Probes** – alle Proben, Harness in `Harness/` (`Ref` spiegelt Unity-Koordinaten).

### Simulation

- `SimulationSystem` ist der einzige Taktgeber: Körper ticken nie selbst.
  `Tick()` sammelt alle Eingaben, `Step()` läuft in festen Phasen: Kontakte →
  `Motor.Simulate` für alle (auf der eigenen Pose, noch unsichtbar) → `SeparateBodies`
  → `Publish` → `SyncHull` → Kampf. `Capture`/`Restore`/`Resimulate` mit
  `WorldSnapshot` sind die Rollback-Grundlage.
- `MotorState` muss **jedes** Motorfeld enthalten; die `DeterminismProbe` prüft
  Rollback bitgenau und hat Mutationstests.
- `TickClock` bildet Unitys Player-Loop nach (Akkumulator, max. 0,333 s pro Frame,
  `dt = 0.025f`); `ClockProbe` misst das gegen `Reference/unity/dump/clock.txt`.
  Die Darstellung interpoliert über `TickInterpolation.Alpha` und wirkt nie zurück.
- `UMath` rechnet Unitys Mathf/Vector3/Quaternion-Formeln Operation für Operation nach;
  Strides eigene Helfer runden anders (z. B. `Normalize`). In der Simulation `UMath`
  verwenden.
- `WorldQueries` beantwortet die Abfragen mit Bepu, aber mit PhysX-Semantik
  (Startüberlappung = Abstand 0 mit Normale gegen den Sweep, Strahl aus dem Inneren
  sieht nichts, stabile Sortierung). Körperhüllen sind Statics auf Layer 6
  (`Hittable`), die `SyncHull` per Teleport setzt.
- Simulationswerte nie aus dem `TransformComponent` zurücklesen.

### Koordinaten

Stride-Konvention (−Z vorn). Unity → Stride exakt: `z → −z`, Gier → −Gier,
Quaternion `(x,y,z,w) → (−x,−y,z,w)`. Jede Spiegelstelle im Code ist kommentiert.

### Assets ohne GPU

Kollision und Körper liegen in eigenen Prefabs **ohne Modelle**
(`KotH.Shared/Assets/{Arena,Bodies}`), damit `HeadlessWorld` sie ohne Grafik lädt.
Die Client-Prefabs verschachteln sie und legen die Modelle darüber.

**Quelle aller Assets ist Game Studio** (Lucas Entscheidung, 16.10.2026). Die
Generatoren aus dem Umzug (`Tools/generate_*.py`, in der Git-Geschichte bis
`577ed3f`) sind entfernt; nicht wiederbeleben, sie überschrieben jede Änderung
aus Game Studio.

Der `GraphicsCompositor` hat bewusst **keinen `Archetype`**: Game Studio setzt
sonst nicht markierte Werte auf den Archetyp zurück, und Tonemapping und Bloom
gehen beim Speichern an.

### Game Studio (Wine/Proton)

- Steam-Verknüpfung „Stride Game Studio 4.4.0“, Prefix `compatdata/2392464373`,
  GE-Proton11-6; darin das Windows-SDK .NET 11 RC2. Projekt: `Z:\var\home\lr\KotH\KotH.slnx`.
  Game Studio startet sich für das net11-Projekt selbst auf .NET 11 neu.
- Startoptionen: `NUGET_PACKAGES=Z:/home/lr/.local/share/stride-winefix/packages
  MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false %command%`.
- Unter .NET 11 hilft `stride-wine-gcfix` nicht mehr gegen den GC-Hänger
  (stride#3599). Der Editor lädt deshalb `Stride.Core` 4.4.0 + Lucas PR #3600 aus
  `~/.local/share/stride-winefix/packages` (Symlinks auf `~/.nuget/packages`,
  nur `stride.core/4.4.0` ist eine Kopie). Das `.nuget` im Prefix ist ein Symlink
  auf `~/.nuget`: dort nie Dateien ersetzen, das trifft die Linux-Builds.
- Aus der Shell nur mit `env -u DOTNET_ROOT` starten, sonst sucht der Windows-Host
  die Laufzeit im Linux-.NET.
- Speichern schreibt CRLF; `.gitattributes` normalisiert auf LF.

### Client-HUD

`HudRenderer` zeichnet das HUD per SpriteBatch nach der Szene (`IHudElement`), weil
Strides UI-Tiefenversatz unter Vulkan kippt. Zahlen im deutschen Format über
`HudRenderer.Numbers`.

## Git

Commit-Messages auf Deutsch; nach `origin main` pushen, wenn Luca das freigibt.
