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
- Restpunkte nicht ungefragt angehen. `Bildschirmfoto_*.png` und
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
  Das Skript baut immer `--no-incremental`: sonst bleibt nach neuen Skriptfeldern das alte
  Szenen-Bundle liegen, und der AOT-Client stürzt beim Laden von `MainScene` ab.
- Server: ohne Argument Echtzeit bis Strg+C, `--seconds N`, oder `--ticks N`
  (so schnell wie möglich, gibt µs/Tick, B/Tick und den exakten Endzustand aus).
  Lasttest: `--bodies N --warmup N` mit `BenchBot`s (geseedeter Zufall), Endzustands-Hash
  muss unter JIT und AOT gleich sein; `KOTH_TRACE=BotName`, `KOTH_FALLS=1`.
  Netz: `--host [--port N] [--min-players N]`, `--join ADRESSE[:PORT]`, `--bot`, `--name`
  (`KotH.Server/NetPlay.cs`), loggt jede 400. Prüfsumme zum Vergleich der Prozesse.
- Client im Netz: `KotH --host`/`--join ADRESSE[:PORT]`, `--name`, `--port`,
  `--min-players`, `--display N` (Monitor von links). Ohne diese Argumente offline wie immer.
  Zwei Clients auf einem PC: `--host --min-players 2 --display 0` und
  `--join 127.0.0.1 --display 1`, beides zusammen `Tools/two_clients.sh [aot|jit]`. JIT-Client und AOT-Client spielen zusammen, wenn der
  JIT-Client mit `DOTNET_EnableAVX=0` läuft (sonst andere `BuildId`).
- Client-Testhilfen in `KotH.Linux/KotHApp.cs`: `KOTH_SHOT` (Screenshot, F3-Anzeige an,
  listet danach alle Schilder mit Text und Position), `KOTH_SHOT_YAW`/`_PITCH`/`_FOV`/
  `_SECONDS` (Standard 3 s), `KOTH_SHOT_THIRD=1`, `KOTH_SHOT_AT=x,y,z`,
  `KOTH_SHOT_SWING=1` (ein Schlag, meldet Treffer), `KOTH_SHOT_SHAKE=1` (Treffer kurz vor
  dem Bild, meldet die Kippung; nur mit `--host`, offline fehlt `DamagePercent`),
  `KOTH_SHOT_BLINK=1` (alle Figuren blinzeln, das Bild zeigt geschlossene Lider),
  `KOTH_SHOT_MOVE=x,y[,sprint]` (hält die Bewegungstasten, D und W sind +1),
  `KOTH_SHOT_CROUCH=s` (Ducken ab s Sekunden gehalten; aus dem Sprint ein Gleiten),
  `KOTH_SHOT_JUMP=s[,s…]` (Sprungtaste zu diesen Zeiten; Doppelsprung = zwei schnelle Drücke in der Luft),
  `KOTH_SHOT_TURN=grad,s[,kreis]` (Blick in First Person um grad drehen, mit `KOTH_SHOT_THIRD` erst
  danach Third Person, Kamera um kreis Grad um die Figur; Startblick 0 lassen, `KOTH_SHOT_YAW` greift
  erst nach dem ersten Bild), `KOTH_SIZE=WxH`, `KOTH_VSYNC=0|1`,
  `KOTH_FRAMELOG=datei`.
- Client: kein Splash (asynchrones Laden + `WaitIdle` ohne QueueLock gab
  `VK_ERROR_DEVICE_LOST`), Renderer-Parallelität 6 (gemessen, Lucas Wahl).

## Tests: Proben und Regression

Es gibt keine Unit-Tests; geprüft wird mit **Proben** gegen die eingefrorene Unity-Referenz.

```bash
Bin/Probes/Release/KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|slide|sprint|melee|step|determinism|clock|stairrun|rampfoot|crouchedge|knockback|thirdperson|wallknock|capture|netstate|netplay>
Bin/Probes/Release/KotH.Probes determinism hashes   # Hashkette + Befehlssatz

.work/tools/stride_runs.sh 3                        # alle Bewegungsproben 3x -> .work/regress/runK
R=Reference/unity/runs
python3 Tools/verdict.py $R/run1,$R/run2,$R/run3 .work/regress/run1   # Urteile gleich?
python3 Tools/regress.py $R/run1,$R/run2,$R/run3 .work/regress/run1   # Zahlen in 5 mm / 1 %?
```

- Exit-Code 0 heißt bestanden. Ziel: alle Urteile gleich (94/94), bekannte
  Abweichungen mit Ursache stehen in `Reference/REGRESSION.md`, ebenso die gewollten
  Änderungen gegenüber Unity (deren Probenfälle haben keine Unity-Referenz,
  `stairrun`, `rampfoot`, `crouchedge`, `thirdperson`, `wallknock`, `capture`, `netstate` und `netplay` laufen
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
  `WorldSnapshot` sind die Rollback-Grundlage. Kein statischer Zustand: Körperliste und
  Tickzähler gehören dem System, mehrere Welten laufen in einem Prozess.
  `Restore` stellt auch Mitgliedschaft und Reihenfolge der Körperliste her (unbekannte
  Körper werden geparkt). Netcode in `KotH.Shared/Net`: `StateCodec` (Snapshot als Bytes,
  `MotorState`/`BodyState` mit `Pack = 1`), Körper heißen dort nach `NetId`.
- Netcode-Kern (Rollback im Stern, Lucas Entscheidung): `HostSession` bestätigt Ticks
  (Eingaben, Beitritte, Abgänge), vergleicht Prüfsummen alle 40 Ticks und schickt bei
  Desync den Zustand; `PeerSession` (auch der Host selbst über `LocalTransport`) sagt
  fremde Eingaben vorher und rollt zurück (Ring 128, höchstens 15 Ticks voraus).
  `INetTransport` mit `MemoryNetwork` für Proben; Peers legen Körper in NetId-Reihenfolge
  an (Dummys 1–3, Spieler ab 4), sonst weicht die Abfrage-Ordnung ab. Mitspielen nur mit
  gleicher `BuildId` (MVID + Befehlssatz).
- Über UDP: `LiteNetTransport` (LiteNetLib 2.1.4). Unzuverlässige Datagramme höchstens
  `NetProtocol.MaxDatagram` (1200 B, das Speichernetz wirft darüber); der Host packt
  bestätigte Ticks vom neuesten rückwärts und wiederholt ab dem ältesten unquittierten
  erst, wenn die Quittung überfällig ist. Der Host klemmt Eingaben (`Sanitize`), der Peer
  seine eigenen genauso. Wartet ein Peer (15 Ticks voraus), bleibt seine Uhr stehen.
  Im Client ist `GameRoot` die `INetWorld`, `NetClient` hält die Sessions; das HUD zeigt
  den Sieg nur aus dem bestätigten Stand (`TryConfirmedMatch`). Kein Glätten von
  Korrekturen (Lucas Entscheidung).
- `HillMatch` (Capture-Zone, `KotH.Shared/Match`) tickt am Ende von `Step()`; Punkte
  pro Körper in `BodyState`, Runde als `MatchState` im Snapshot. Nur mit
  `HeadlessWorld.StartMatch()` bzw. in `GameRoot`, Bewegungsproben laufen ohne.
  Phasen `Running`/`Over` (0/1 wie das alte Flag) und für den Mehrspieler
  `Waiting`/`Countdown`, nur mit `MinPlayers` > 0 (steht im `MatchState`, kommt also
  mit dem Zustand zum Beitretenden). Spieler sind Körper ab `NetWorld.FirstPlayerId`.
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

Die Kamera wackelt bei jedem eingesteckten Treffer gleich stark (First und Third
Person, `PlayerCamera`, „Wackeln bei Treffer“). Auslöser ist `DamagePercent.LastHitTick`,
kein Zustand: ein Rollback behält ihn, im Netz kommt der Treffer mit der Bestätigung.

### Figur und Animation

Die Pilzfigur (`Mushroom.glb`) hat ein Skelett (`MushroomSkeleton.sdskel`, mit Ellenbogen
und Knien) und 17 Clips: `MushroomIdle` (Atmen 4 s, Wiegen 8 s), `MushroomRest`
(Grundhaltung: Luft, Rutschen am Steilhang), Gehen/Rennen je vorwärts, rückwärts,
links, rechts (Gang nach Sanji, Lucas Wunsch), `MushroomCrouch` (Hocke mit Atmen, auch in
der Luft), `MushroomCrouchWalk` (ein Clip für alle Richtungen, 3 Schritte/s) und
`MushroomSlide` (Sanji-Grätsche), `MushroomFall` (nur abwärts, Arme schräg zur Seite und
nach vorn, balanciert leicht) und `MushroomLand` (7 cm eingefedert: nach jeder Landung nach
mindestens 0,15 s Luft ein gleich starker Impuls über alles, 0,06 s rein, weg nach 0,3 s),
`MushroomJump` (beim Aufsteigen, nach Sanji: ein Bein gestreckt, Knie hoch, Arme an der Hüfte)
und `MushroomSkyWalk` (Doppelsprung als Tritt in die Luft, Impuls wie die Landung; erkannt am
Schub nach oben in der Luft, die Simulation meldet nichts). Dabei stößt die Figur gelbe Sporen aus den Füßen
(`Assets/Effects/SporeBurst.sdprefab`, ein Stoß von 60 Partikeln im Weltraum, Textur `Spore`
256 px mit scharfer Kante; „Sporen beim Doppelsprung“ in `PlayerVisualSystem`). Geduckt macht die Pose die Figur nur etwa 20 cm niedriger
(Beine 35 cm); `PlayerVisualSystem` staucht sie fest auf 75 % und 90 % Breite („Höhe geduckt“,
„Breite geduckt“), Kopf und Kappe ragen etwa 30 cm über die 0,9-m-Hülle (Lucas Wahl: Proportionen
vor Passform, dort zählt kein Treffer); die Füße bleiben auf dem Boden der Hülle. `PlayerVisualSystem` spielt alle zugleich
und setzt die Gewichte nach Tempo und Richtung (Strides Mischen normiert auf die Summe);
die Lauf-Clips laufen mit `TimeFactor` 0 auf einer gemeinsamen Phase aus der Strecke
(Gehen 3,5, Rennen 6 Schritte/s). Das Spiel selbst bewegt vier Knochenarten: den Mund (`Mouth`, atmet ruhig in zwei
überlagerten Wellen; eine Hautfläche deckt den gemalten Mund ab, die Mundfläche schwebt 5 mm
davor und überbrückt die Mulde im Netz), die Blätter (`Leaf.S0`–`S5` am Rock, `Leaf.C0`–`C3`
am Kragen, zeigen nach oben mit dem Drehpunkt oben; Federn im Fahrtwind und im Wind der
Drehung, „Blätter“ in `PlayerVisualSystem`), die Lider
(Blinzeln zufällig alle 2–10 s, offen auf einen Punkt geschrumpft) und `Look` unter dem Kopf (folgt
`SimulationBody.LookPitch`, dem Nickwinkel der Eingabe, kein Zustand; im Stehen dreht er auch bis
30° zur Seite, erst darüber zieht er den Körper mit, in Bewegung folgt der Körper dem Blick,
„Kopfdrehung höchstens“, „Körper folgt Blick“; der Motor schaut weiter in Blickrichtung). Die Clips dürfen
deshalb **keine Kanäle für Lider, Blätter, `Mouth` und `Look`** haben. Erzeugt werden Modell, Clips,
Lid-Textur und die Knotenliste des Skeletts mit `.work/rig/build.sh` (Blender headless,
`rig.py`, entfernt diese Kanäle, kopiert nach `Resources/Models`). In Third Person dreht
sich die eigene Figur in Laufrichtung; Seitwärts- und Rückwärts-Clips sieht man bei
Spielern in First Person. Nur Darstellung, die Simulation sieht davon nichts.

## Git

Commit-Messages auf Deutsch; nach `origin main` pushen, wenn Luca das freigibt.
