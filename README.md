# KotH

Ein King-of-the-Hill-Spiel in der Ego-Perspektive. Wer den Gipfel hält, gewinnt.
Der aktuelle Stand ist ein Prototyp: eine Testmap mit Berg, Rampen und Treppen
(`--map Test`; Standard ist die Map `Neu`, ein 136 m hoher Blockberg auf einer Insel
nach einer Minecraft-Map), Bots, die
um den Gipfel kämpfen, alle als Fliegenpilz-Figuren, Bewegung mit Sprint,
Gleiten, Ducken, gestuftem Sprung und Doppelsprung, Körperstöße zwischen
Spielern sowie ein Nahkampfschlag. Der Schlag erhöht das Schadensprozent des
Getroffenen und stößt ihn umso weiter weg, je höher es schon war: bei 100 %
12 m weit und 1,2 m hoch, linear bis 1000 %; bei 0 % hüpft er nur so hoch
wie bei 1 %. Ab 10 % zählt auch das Tempo
beider auf der Stoßlinie: Wer heranläuft, stößt stärker; wer als Getroffener
heranläuft, fliegt weniger weit. Bis 20 % steigt der Anteil von der Hälfte auf
das volle Tempo. Wer gerade springt oder fällt, behält das: Der Stoß nach oben
kommt auf seinen Sprung obendrauf. Wer getroffen wird, nimmt Oberkörper und Kopf
kurz weg vom Schlag, in acht Richtungen (nur Darstellung). Jede Figur trägt in der
rechten Hand einen Bambusstab, auch an den Armen in First Person; das Blatt daran
weht im Fahrtwind wie die Blätter am Rock (nur Darstellung, der Schlag bleibt gleich).
Jeder Schlag schwingt den Stab: meist von oben rechts nach unten links, jeder vierte
von unten links nach oben rechts, jedes Mal etwas anders (nur Darstellung).

**Spielregel.** Oben auf dem Plateau, wo die Rampen hinaufführen, liegt die
Capture-Zone, leuchtend umrandet. Wer allein in ihr steht, nimmt sie in 3 s ein und
bekommt danach einen Punkt pro Sekunde, solange er drin bleibt, auch wenn andere
dazukommen. Kommt während der 3 s jemand dazu, ist sie umkämpft, keiner punktet, und
das Einnehmen beginnt von vorn; geht der Halter hinaus, ist sie wieder frei. Wer zuerst 100 Punkte hat,
gewinnt; nach 20 Minuten gewinnt, wer die meisten hat, bei Gleichstand ist die
Runde unentschieden. Nach 5 s Siegeranzeige beginnt die nächste Runde, alle stehen
wieder an ihrem Startpunkt. Die Werte stehen an der
Komponente `CaptureZone` im Collision-Prefab der Map und sind in Game Studio einstellbar.
Wer aus der Arena fliegt und unter 5 m unter den Boden fällt („Respawn unter“), steht
sofort wieder an seinem Startplatz; Schadensprozent und Punkte bleiben.

**Bots.** Statt der früheren Trainingspuppen spielen Bots mit (offline und beim Host
3, `--bots N`, `--bot-seed N`). Sie wollen ab Rundenbeginn auf den Gipfel, finden den
Weg über ein Navmesh (Rampen, Treppen, Sprünge und Doppelsprünge an Kanten), sprinten,
gleiten in Angriffe hinein und schlagen jeden, auch einander; sie zählen als Spieler.
In der Lobby und im Countdown schlagen Bots nicht. Ein Bot sieht die Welt 0,2 s verzögert, wie ein Mensch mit Reaktionszeit. Stärke,
Zielfehler und Navmesh stehen an der Komponente `BotSettings` (Entity „Bots“ im
Collision-Prefab der Map). Die Bots starten mit Lücken in beiden Reihen (`NetWorld.BotSlots`),
nicht dicht nebeneinander.

Im Mehrspieler wartet die Runde auf 4 Spieler (die Bots des Hosts zählen mit), dann läuft
ein Countdown von 60 s; geht vorher einer, wird wieder gewartet. Bei 0 stehen alle an
ihrem Startplatz (`PlayerSpawn0` bis `PlayerSpawn11`, zwei Reihen hinter dem Spawn).
Beitreten geht beim Warten, im Countdown, nach einem Sieg und in der ersten Minute
einer Runde. Offline beginnt die Runde sofort.

Musik: Vor dem Spiel (Warten, Countdown und die Pause nach einem Sieg) läuft ein
Lobby-Track in Schleife, bei Rundenbeginn ein fester Spielbeginn-Track, danach die
übrigen sieben gemischt, jeder einmal, bevor einer wiederkommt. Jeder Wechsel
blendet in 3,5 s über. Alle Tracks sind auf −15,5 LUFS normalisiert; Lautstärke und
Auswahl stehen in Game Studio an der Entity `Music` (`MusicPlayer`).

| | |
|---|---|
| Engine | [Stride](https://www.stride3d.net/) 4.4 mit Bepu-Physik |
| Laufzeit | .NET 11 (RC2), NativeAOT, Befehlssatz x86-64-v2 |
| Grafik | Vulkan |
| Plattform | Linux (x64) |
| Simulation | fest 40 Hz, deterministisch, rollback-fähig |
| Lizenz | LGPL-3.0-or-later |

Das Projekt ist von Unity 6000.3 auf Stride umgezogen. Die Logik ist dabei
gleich geblieben; gemessen wurde das gegen eingefrorene Unity-Messungen (siehe
[Proben und Regression](#proben-und-regression)). Das Unity-Projekt selbst ist
entfernt und nur noch in der Git-Geschichte (bis `577ed3f`); die Messungen liegen
in `Reference/unity/`. Mehrspieler kommt später als Peer-to-Peer:
Ein Peer hostet, ein dedizierter Server ist nicht geplant.

## Steuerung

| Taste | Aktion |
|---|---|
| W A S D / Pfeiltasten | laufen |
| Maus | umsehen |
| Leertaste | springen; länger halten springt höher, in der Luft zweimal tippen ist der Doppelsprung |
| Linke Umschalttaste | sprinten (antippen genügt, der Sprint hält, solange das Tempo hält) |
| E | ducken; im Sprint antippen = gleiten; aus dem Sprint-Sprung gehalten landen = gleiten |
| Linksklick | schlagen |
| Mausrad zurück / vor | Third-Person / zurück in die Ego-Perspektive. In Third-Person kreist die Kamera frei, die Figur läuft in Kamerarichtung, der Schlag trifft, was das Fadenkreuz zeigt (Reichweite vom Auge) |
| F3 | Leistungsanzeige (FPS, Frametime, 1 %-Low) |
| F11 | zwischen Vollbild (randlos, Standard) und Fenster umschalten |
| Escape | einmal: Maus freigeben, zweimal: beenden. Ein Klick fängt die Maus wieder ein. |

## Voraussetzungen

- **.NET SDK 11.0.100-rc.2** in `~/.dotnet` (von `global.json` festgelegt).
  Ist er nicht im `PATH`: `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.
- Vulkan-Treiber.
- Für die **NativeAOT-Builds**: eine Distrobox namens `aot` (Fedora 44) mit
  `clang`, `lld`, `zlib-devel` und `libva` (für Strides ffmpeg beim Übersetzen der Musik). Der normale JIT-Build braucht sie nicht.
- Zum Bearbeiten von Szene, Prefabs und Tuning: **Stride Game Studio** 4.4.
  Unter Linux läuft es über Wine bzw. Proton; geöffnet wird `KotH.slnx`. Das
  Prefix braucht das Windows-SDK von .NET 11. Unter .NET 11 friert der Editor
  unter Wine ohne den Fix aus [stride#3600](https://github.com/stride3d/stride/pull/3600)
  ein ([stride#3599](https://github.com/stride3d/stride/issues/3599)).

## Bauen und starten

```bash
# Client (JIT)
dotnet build KotH.Linux -c Release
cd Bin/Linux/Release && ./KotH

# NativeAOT: Client, Server und Proben, gelinkt in der Box "aot"
Tools/publish_aot.sh                  # alle drei
Tools/publish_aot.sh linux            # nur der Client -> Bin/Linux/aot/KotH
```

Die AOT-Builds landen in `Bin/{Linux,Server,Probes}/aot`. Der Client ist dort
ein einzelnes Binary ohne installierte .NET-Laufzeit. Stürzt ILC beim Publish
ab, was gelegentlich passiert, das Skript einfach erneut starten.

### Server

Der Server lädt Arena und Dummy (mit `--bots N` stattdessen Bots) ohne Fenster und ohne GPU, tickt mit 40 Hz und
spielt die Runde um die Zone mit (Stand alle 10 s und am Ende).
Es ist dieselbe Schleife, die später ein hostender Peer fährt.

```bash
dotnet build KotH.Server -c Release
Bin/Server/Release/KotH.Server                # Echtzeit bis Strg+C
Bin/Server/Release/KotH.Server --seconds 30   # Echtzeit für 30 s
Bin/Server/Release/KotH.Server --ticks 2000   # so schnell wie möglich, mit µs/Tick,
                                              # Bytes/Tick und exaktem Endzustand
Bin/Server/Release/KotH.Server --bodies 64 --warmup 400 --ticks 4000 --map Test
                                              # Lasttest: 64 Bots laufen, springen,
                                              # gleiten und schlagen nach Zufall
```

`--map NAME` wählt die Map (Standard `Neu`), auch beim Hosten.

Der Lasttest misst erst nach `--warmup` Ticks und gibt Median und 99 % je Tick
aus, dazu einen Hash des Endzustands, der unter JIT und AOT gleich sein muss.
`KOTH_TRACE=Bot5` schreibt die Spur eines Bots pro Tick, `KOTH_FALLS=1` jede
Stelle, an der ein Bot unter den Boden gerät.

### Mehrspieler

Ein Spieler hostet, die anderen treten über seine IP bei (UDP, Standardport 7777;
übers Internet muss der Host den Port im Router freigeben). Alle brauchen dasselbe
Build, sonst lehnt der Host ab. Bis zu 12 Spieler.

```bash
cd Bin/Linux/Release
./KotH --host --name Luca                     # hosten und mitspielen
./KotH --join 192.168.0.5 --name Gast         # beitreten, auch ADRESSE:PORT
./KotH --host --min-players 2 --display 0     # Runde ab 2 Spielern, linker Monitor
./KotH --join 127.0.0.1 --display 1           # zweiter Client, rechter Monitor
```

`Tools/two_clients.sh` startet beide Fenster auf einmal (AOT, mit `jit` die
JIT-Builds, danach optional die Map, z. B. `Tools/two_clients.sh aot Test`) und
schließt beide, wenn eins zugeht.

Die Map wählt der Host mit `--map NAME` (Standard `Neu`, die alte Arena ist `Test`);
wer beitritt, bekommt sie vom Host und lädt sie selbst.

`--display N` zählt die Monitore von links, 0 ist der linke. `--port N` ändert
den Port. Ohne `--name` heißt man wie der Benutzer; doppelte Namen bekommen beim
Host eine Nummer („Luca 2“). Esc gibt die Maus frei, um ins andere Fenster zu
wechseln; ein Klick fängt sie wieder. F3 zeigt Ping, Vorlauf und Rollbacks.

Der Server kann dasselbe ohne Fenster, mit einem Bot als Spieler:

```bash
Bin/Server/Release/KotH.Server --host --bot              # hosten, mit 3 Bots (--bots N)
Bin/Server/Release/KotH.Server --join 127.0.0.1 --bot    # beitreten
```

### Testhilfen im Client

Mit `KOTH_SHOT=bild.png` zeigt der Client die F3-Anzeige, speichert nach
`KOTH_SHOT_SECONDS` (Standard 3 s) einen Screenshot und beendet sich.
`KOTH_SHOT_YAW`, `KOTH_SHOT_PITCH` und `KOTH_SHOT_FOV` halten den Blick fest.
`KOTH_SHOT_SHAKE=1` lässt die eigene Figur kurz vor dem Bild getroffen werden und
meldet, wie weit die Kamera wackelt.
`KOTH_SHOT_BLINK=1` lässt alle Figuren so blinzeln, dass das Bild die Lider zeigt.
`KOTH_SHOT_STRIKE=s[,s…]` lässt alle Figuren zu diesen Zeiten mit dem Stab schlagen (nur Darstellung).
`KOTH_SHOT_MOVE=x,y[,sprint]` hält die Bewegungstasten (D und W sind +1), etwa
`KOTH_SHOT_MOVE=0,1,sprint` mit `KOTH_SHOT_THIRD=1` für ein Bild im Rennen.
`KOTH_SHOT_CROUCH=s` drückt ab s Sekunden Ducken und hält es, etwa mit
`KOTH_SHOT_MOVE=1,0,sprint`, `KOTH_SHOT_CROUCH=2.2` und `KOTH_SHOT_SECONDS=2.5` für ein Bild im Gleiten.
`KOTH_SHOT_JUMP=s[,s…]` drückt zu diesen Zeiten die Sprungtaste; der Doppelsprung braucht in der
Luft zwei schnelle Drücke, etwa `KOTH_SHOT_JUMP=2.0,2.2,2.25` mit `KOTH_SHOT_SECONDS=2.31`.
`KOTH_SHOT_TURN=grad,s[,kreis]` dreht den Blick in First Person zur Zeit s um grad; mit
`KOTH_SHOT_THIRD=1` geht die Kamera erst danach nach hinten und kreist um kreis Grad um die Figur,
etwa `KOTH_SHOT_TURN=30,2.0,170` für Kopf gedreht, Körper gerade, von vorn gesehen.
`KOTH_SHOT_VIDEO=s` schreibt zusätzlich die letzten s Sekunden vor dem Bild als Rohbilder mit
30 fps nach `bild.png.rgba` (Größe und Format stehen im Log), daraus wird ein Video mit
`ffmpeg -f rawvideo -pix_fmt bgra -s 1280x720 -r 30 -i bild.png.rgba -pix_fmt yuv420p video.mp4`.

Für Leistungsmessungen: `KOTH_SIZE=1920x1080` legt die Fenstergröße fest,
`KOTH_VSYNC=0|1` schaltet V-Sync, und `KOTH_FRAMELOG=datei` schreibt jede
Frametime in eine Datei. Strides Renderer läuft mit höchstens 6 Threads, das war
gemessen der beste Kompromiss aus FPS und CPU-Last; `STRIDE_MAX_PARALLELISM`
überschreibt das. Einen Splash-Screen gibt es nicht: Mit ihm lädt Stride die
Szene asynchron, und das brachte unter Vulkan gelegentlich `VK_ERROR_DEVICE_LOST`.

## Projektstruktur

```
KotH.Shared/   die gesamte Simulation, ohne Grafik: Motor, Physikabfragen, Kampf,
               Tick-Treiber, Rollback; dazu Kollisions-Prefabs ohne Modelle
KotH.Game/     Client: Eingabe, Kamera, Darstellung, HUD, Szene und Materialien
KotH.Linux/    ausführbarer Client (Vulkan, NativeAOT)
KotH.Server/   Konsolen-Host ohne GPU
KotH.Probes/   Messproben gegen die Unity-Referenz
Tools/         Regressionsvergleich, AOT-Publish
Reference/     eingefrorene Unity-Messungen und der Regressionsbericht
```

Einige Kernpunkte der Architektur:

- **Ein Taktgeber.** `SimulationSystem` tickt alle Körper; kein Körper tickt
  sich selbst. Ein Tick läuft in festen Phasen: Kontakte, Bewegung aller Körper
  auf ihrer eigenen Pose, Trennen, Veröffentlichen, Kampf.
- **Rollback.** `Capture`, `Restore` und `Resimulate` über `WorldSnapshot`. Der
  `MotorState` enthält jedes Motorfeld, die DeterminismProbe prüft das bitgenau.
  Der Snapshot hält auch die Runde (`HillMatch`) und die Punkte jedes Körpers.
- **Unitys Rechenweg.** `UMath` bildet Unitys Mathe-Formeln Operation für
  Operation nach; `WorldQueries` beantwortet die Physikabfragen mit Bepu, aber
  mit der PhysX-Semantik, auf die der Motor abgestimmt ist.
- **Ohne GPU ladbar.** Kollision und Körper stehen in eigenen Prefabs ohne
  Modelle, damit Server und Proben sie ohne Grafik laden. Der Client legt die
  Modelle darüber.
- **Koordinaten.** Stride-Konvention mit −Z als vorn. Unity-Werte werden exakt
  gespiegelt: `z → −z`, Quaternion `(x, y, z, w) → (−x, −y, z, w)`.

### Assets

Szene, Prefabs, Materialien und Tuningwerte werden in **Stride Game Studio**
bearbeitet. Für den Umzug wurden sie einmalig aus Skripten erzeugt, die Arena
bitgenau aus den Unity-Dumps; seitdem ist Game Studio ihre einzige Quelle.

Jede Map hat einen Ordner `Maps/NAME`: `NAMECollision` in KotH.Shared (Kollision,
Startplätze, Zone, Bot-Einstellungen; läuft auch ohne Grafik im Server und in den
Proben) und `NAMEVisual` in KotH.Game (was man sieht). Eine neue Map braucht beide
als Root-Asset (Game Studio: „Include in build as root asset“); `--map NAME` findet
sie dann über den Ordnernamen.

Der Boden der Map `Neu` kommt aus einem Skript: `python3 Tools/maps/mountain.py`
schreibt das Modell `Resources/Models/Mountain.glb` (Vertexfarben mit Kantenschatten
wie in Minecraft) und die Kollision `Maps/Neu/NeuTerrain.sdprefab` (Boxen in Chunks
von 8 × 8 m), sonst nichts; Startplätze, Zone und Optik-Prefab bleiben in Game Studio.
Es legt keine Wege an: Der Hang ist so steil, dass man sich mit Sprung und Doppelsprung
selbst einen Weg suchen muss. Das Skript prüft, ob der Gipfel aus genug Richtungen
erreichbar ist, und bricht sonst ab. Die Werte stehen oben im Skript (Höhe, Fuß,
Steigung, Rauschen, Schneelinie, Bäume, Seed). Bots kommen dort noch nicht hinauf.

## Proben und Regression

Es gibt keine Unit-Tests. Geprüft wird mit **Proben**, die den Motor mit
geskripteter Eingabe fahren und messen. Exit-Code 0 heißt bestanden.

```bash
dotnet build KotH.Probes -c Release
export DOTNET_EnableAVX=0                     # siehe unten
Bin/Probes/Release/KotH.Probes step           # eine Probe
Bin/Probes/Release/KotH.Probes determinism hashes
```

Proben: `worldquery`, `rotation`, `slopejump`, `crest`, `impact`, `jumpheight`,
`slide`, `sprint`, `melee`, `step`, `determinism`, `clock`, `stairrun`, `rampfoot`,
`crouchedge`, `knockback`, `thirdperson`, `wallknock`, `capture`, `netstate`, `netplay`.

Vergleich mit der Unity-Referenz:

```bash
.work/tools/stride_runs.sh 3                  # alle Bewegungsproben dreimal
R=Reference/unity/runs
python3 Tools/verdict.py $R/run1,$R/run2,$R/run3 .work/regress/run1   # Urteile
python3 Tools/regress.py $R/run1,$R/run2,$R/run3 .work/regress/run1   # Zahlen, 5 mm / 1 %
```

Stand: alle 94 Urteile wie in Unity. Jede verbleibende Zahlenabweichung und
jede gewollte Änderung gegenüber Unity ist mit Ursache in
[`Reference/REGRESSION.md`](Reference/REGRESSION.md) beschrieben.

**Determinismus und Befehlssatz.** Gleitkommaergebnisse hängen am Befehlssatz:
AVX2/FMA rundet anders als SSE. Alle AOT-Builds sind deshalb fest auf
x86-64-v2 gebaut. Ein JIT-Lauf auf einer AVX2-CPU rechnet nur mit
`DOTNET_EnableAVX=0` genauso. Die Hashkette der DeterminismProbe muss unter JIT
und AOT gleich sein; derzeit lautet sie `880D0C64389C53E3`. Für Peer-to-Peer
heißt das: Alle Peers brauchen dasselbe Binary.

## Lizenz

KotH ist freie Software unter der **GNU Lesser General Public License,
Version 3 oder später** (LGPL-3.0-or-later). Den Lizenztext enthält
[`COPYING.LESSER`](COPYING.LESSER). Die LGPL ergänzt die GNU General Public
License Version 3, die in [`COPYING`](COPYING) liegt.

Copyright © 2026 Luca Rohde

Ausgenommen sind mitgelieferte Fremdbestandteile unter ihren eigenen Lizenzen:

- `KotH.Game/Resources/Fonts/OpenSans-Regular.ttf`: Open Sans, Apache License 2.0
  (`KotH.Game/Resources/Fonts/LICENSE.txt`).
- `KotH.Game/Resources/skybox_texture_*.dds`: aus der Stride-Projektvorlage, MIT.
- `KotH.Game/Resources/Music/*.mp3`: von Pixabay unter der
  [Pixabay Content License](https://pixabay.com/service/license-summary/), auf −15,5 LUFS normalisiert:
  - Hauptmenü (noch nicht eingebaut): [Jazz Fusion Piano Cocktail Bar](https://pixabay.com/music/modern-jazz-jazz-fusion-piano-cocktail-bar-560074/)
  - Vor dem Spiel: [Jazz Piano](https://pixabay.com/music/small-drama-jazz-piano-585359/)
  - Spielbeginn: [Fun and Quirky Jazz](https://pixabay.com/music/modern-jazz-fun-and-quirky-jazz-583916/)
  - Während des Spiels: [Jazz Cocktail Bar Music](https://pixabay.com/music/modern-jazz-jazz-cocktail-bar-music-556247/),
    [Acid Jazz Restaurant](https://pixabay.com/music/modern-jazz-acid-jazz-restaurant-552301/),
    [Swing Jazz Sunny Cafe](https://pixabay.com/music/modern-jazz-swing-jazz-sunny-cafe-568159/),
    [Jazz Cocktail Bar Funk](https://pixabay.com/music/funk-jazz-cocktail-bar-funk-559987/),
    [Stylish Funk Smooth Party](https://pixabay.com/music/funk-stylish-funk-smooth-party-no-copyright-620664/),
    [Retro House](https://pixabay.com/music/soft-house-retro-house-201792/),
    [Jazz Coffee Shop Music](https://pixabay.com/music/modern-jazz-jazz-coffee-shop-music-556234/)
