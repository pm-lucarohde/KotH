# KotH

Ein King-of-the-Hill-Spiel in der Ego-Perspektive: Wer den Gipfel hält, gewinnt. Prototyp mit
zwei Maps (Standard `Neu`: ein 123 m hoher Berg auf einer Insel nach einer Minecraft-Map, Blöcke
abgerundet wie im Mod NoCubes; `Test`: die alte Arena mit Rampen und Treppen), Bots und
Fliegenpilz-Figuren mit Bambusstab. Bewegung: Sprint, Gleiten, Ducken, gestufter Sprung,
Doppelsprung, Körperstöße. Der Schlag erhöht das Schadensprozent des Getroffenen und stößt ihn umso
weiter, je höher es ist (bei 100 % 12 m weit und 1,2 m hoch, linear bis 1000 %); ab 10 % zählt auch
das Tempo beider auf der Stoßlinie.

**Spielregel.** Die Capture-Zone liegt oben auf dem Gipfel. Wer allein drin steht, nimmt sie in 3 s
ein und punktet danach jede Sekunde, solange er drin bleibt, auch wenn andere dazukommen; geht er
hinaus, ist sie frei. Sieg mit 100 Punkten oder nach 20 Minuten mit den meisten; nach 5 s beginnt die
nächste Runde. Wer mehr als 5 m unter den Boden fällt, steht sofort wieder an einem Startplatz;
Prozent und Punkte bleiben. Die Werte stehen an der Komponente `CaptureZone` der Map.

**Startplätze.** Auf `Neu` steht beim Rundenbeginn und nach jedem Sturz jeder zufällig auf der Wiese
am Fuß (101–110 m vom Gipfel), mal nah, mal weit von den anderen, aber mindestens 6 m entfernt, und
schaut zum Gipfel. In Lobby und Countdown und auf `Test` gelten die festen Startplätze.

**Bots.** Offline und beim Host spielen 3 Bots mit (`--bots N`, `--bot-seed N`), als Spieler gezählt.
Sie klettern über ein eigenes Kletterraster, jeder auf seiner Route, rechnen jeden Sprung in einem
Modell des Motors vor, lernen aus Fehlsprüngen und steuern wie ein Mensch (fangen sich nach der Landung,
kein Zappeln, Blick mit Trägheit). Auf den Berg brauchen sie im Mittel etwa 1:25.
Weit unter dem Gipfel schlagen sie nur im Vorbeigehen, oben kämpfen sie; in Lobby und Countdown nie.
Sie sehen die Welt 0,2 s verzögert. Einstellungen an `BotSettings` (Entity „Bots“ der Map).

**Mehrspieler.** Die Runde wartet auf 4 Spieler (Bots zählen mit), dann folgt ein Countdown von 60 s.
Beitreten geht beim Warten, im Countdown, nach einem Sieg und in der ersten Minute. Offline beginnt
die Runde sofort. **Musik:** Lobby-Track vor dem Spiel, ein Spielbeginn-Track, danach die übrigen
gemischt, Überblendung 3,5 s (Entity `Music`).

| | |
|---|---|
| Engine | [Stride](https://www.stride3d.net/) 4.4 mit Bepu-Physik, Vulkan, Linux x64 |
| Laufzeit | .NET 11 (RC2), NativeAOT, Befehlssatz x86-64-v2 |
| Simulation | fest 40 Hz, deterministisch, rollback-fähig; Logik wie im früheren Unity-Projekt |

## Steuerung

| Taste | Aktion |
|---|---|
| W A S D / Pfeiltasten | laufen |
| Maus | umsehen |
| Leertaste | springen (länger halten = höher), in der Luft zweimal tippen = Doppelsprung |
| Linke Umschalttaste | sprinten (antippen genügt) |
| E | ducken; im Sprint antippen oder gehalten landen = gleiten |
| Linksklick | schlagen |
| Mausrad | Third Person / zurück in die Ego-Perspektive |
| F3 / F11 | Leistungs- und Netzanzeige / Vollbild oder Fenster |
| Escape | einmal Maus freigeben, zweimal beenden; ein Klick fängt sie wieder |

## Voraussetzungen

- .NET SDK 11.0.100-rc.2 in `~/.dotnet` (`global.json`); sonst
  `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`. Dazu ein Vulkan-Treiber.
- NativeAOT: Distrobox `aot` (Fedora 44) mit `clang`, `lld`, `zlib-devel`, `libva`.
- Szene, Prefabs, Tuning: Stride Game Studio 4.4 (unter Wine/Proton mit dem Windows-SDK von .NET 11
  und dem Fix aus [stride#3600](https://github.com/stride3d/stride/pull/3600)).

## Bauen und starten

```bash
dotnet build KotH.Linux -c Release && cd Bin/Linux/Release && ./KotH   # Client (JIT)
Tools/publish_aot.sh [linux|server|probes]    # NativeAOT -> Bin/{Linux,Server,Probes}/aot
```

Stürzt ILC beim Publish ab (kommt vor), einfach erneut starten. `--map NAME` wählt die Map. **Server** (ohne Fenster und GPU, 40 Hz, dieselbe Schleife wie ein hostender Peer):

```bash
Bin/Server/Release/KotH.Server [--seconds 30 | --ticks 2000] [--bots 3] [--map Test]
Bin/Server/Release/KotH.Server --bodies 64 --warmup 400 --ticks 4000 --map Test   # Lasttest
```

`--ticks` rechnet so schnell wie möglich, mit µs/Tick und Hash des Endzustands (JIT = AOT).

**Mehrspieler** über UDP (Port 7777), bis 12 Spieler, alle mit demselben Build:

```bash
./KotH --host --name Luca                    # hosten und mitspielen (--port, --min-players, --map)
./KotH --join 192.168.0.5 --name Gast        # beitreten, auch ADRESSE:PORT
Tools/two_clients.sh [aot|jit] [MAP]         # zwei Fenster auf einem PC (--display 0 / 1)
Bin/Server/Release/KotH.Server --host --bot  # dasselbe ohne Fenster (--join ADRESSE --bot)
```

Gäste bekommen die Map vom Host. **Testhilfen** im Client (`KOTH_SHOT=bild.png` und Verwandte für
Screenshots, Videos und gescriptete Tasten, `KOTH_SIZE`, `KOTH_VSYNC`, `KOTH_FRAMELOG`) stehen in
`CLAUDE.md`.

## Projektstruktur

```
KotH.Shared/   die gesamte Simulation ohne Grafik; Kollisions-Prefabs ohne Modelle
KotH.Game/     Client: Eingabe, Kamera, Darstellung, HUD, Szene, Shader
KotH.Linux/    ausführbarer Client (Vulkan, NativeAOT)
KotH.Server/   Konsolen-Host ohne GPU
KotH.Probes/   Messproben gegen die Unity-Referenz
Tools/         Regressionsvergleich, AOT-Publish, Generatoren der Map Neu
Reference/     eingefrorene Unity-Messungen und der Regressionsbericht
```

- **Simulation:** `SimulationSystem` tickt alle Körper in festen Phasen, Rollback über `WorldSnapshot`;
  `UMath` rechnet Unitys Formeln nach, `WorldQueries` fragt Bepu mit PhysX-Semantik.
- **Assets:** Game Studio ist die einzige Quelle. Jede Map hat `Maps/NAME` mit `NAMECollision`
  (KotH.Shared, ohne Grafik ladbar) und `NAMEVisual` (KotH.Game), beide als Root-Asset.
- **Map Neu:** `python3 Tools/maps/mountain.py` erzeugt Berg, Meer und Kollision (keine vorgefertigten
  Wege, prüft die Erreichbarkeit des Gipfels), `python3 Tools/maps/textures.py` malt die Texturen.
  Danach alles neu bauen.

## Proben und Regression

Keine Unit-Tests: Proben fahren den Motor mit geskripteter Eingabe; Exit-Code 0 heißt bestanden.

```bash
export DOTNET_EnableAVX=0                         # JIT rechnet sonst anders als AOT
Bin/Probes/Release/KotH.Probes step               # eine Probe; Liste in CLAUDE.md
.work/tools/stride_runs.sh 3                     # alle Bewegungsproben dreimal
R=Reference/unity/runs; python3 Tools/verdict.py $R/run1,$R/run2,$R/run3 .work/regress/run1
```

Stand: alle 94 Urteile wie in Unity; Abweichungen mit Ursache in
[`Reference/REGRESSION.md`](Reference/REGRESSION.md). Alle AOT-Builds sind fest auf x86-64-v2 gebaut,
die Hashkette der DeterminismProbe (`880D0C64389C53E3`) muss unter JIT und AOT gleich sein; im
Netz brauchen alle Peers dasselbe Binary.

## Lizenz

KotH ist freie Software unter der **GNU Lesser General Public License, Version 3 oder später**
(LGPL-3.0-or-later, [`COPYING.LESSER`](COPYING.LESSER), ergänzt die GPL v3 in [`COPYING`](COPYING)).

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
