# KotH

Ein King-of-the-Hill-Spiel in der Ego-Perspektive. Wer den Gipfel hält, gewinnt.
Der aktuelle Stand ist ein Prototyp: Arena mit Berg, Rampen und Treppen, ein
Trainingsdummy, Bewegung mit Sprint, Gleiten, Ducken, gestuftem Sprung und
Doppelsprung, Körperstöße zwischen Spielern sowie ein Nahkampfschlag, der das
Schadensprozent des Getroffenen erhöht.

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
[Proben und Regression](#proben-und-regression)). Unity liegt noch als
Messreferenz im Repo (`Assets/`, `Packages/`, `ProjectSettings/`) und wird zum
Abschluss des Umzugs entfernt. Mehrspieler kommt später als Peer-to-Peer:
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
| F3 | Leistungsanzeige (FPS, Frametime, 1 %-Low) |
| Escape | einmal: Maus freigeben, zweimal: beenden. Ein Klick fängt die Maus wieder ein. |

## Voraussetzungen

- **.NET SDK 11.0.100-rc.2** in `~/.dotnet` (von `global.json` festgelegt).
  Ist er nicht im `PATH`: `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.
- Vulkan-Treiber.
- Für die **NativeAOT-Builds**: eine Distrobox namens `aot` (Fedora 44) mit
  `clang`, `lld` und `zlib-devel`. Der normale JIT-Build braucht sie nicht.
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

Der Server lädt Arena und Dummy ohne Fenster und ohne GPU und tickt mit 40 Hz.
Es ist dieselbe Schleife, die später ein hostender Peer fährt.

```bash
dotnet build KotH.Server -c Release
Bin/Server/Release/KotH.Server                # Echtzeit bis Strg+C
Bin/Server/Release/KotH.Server --seconds 30   # Echtzeit für 30 s
Bin/Server/Release/KotH.Server --ticks 2000   # so schnell wie möglich, mit µs/Tick,
                                              # Bytes/Tick und exaktem Endzustand
```

### Testhilfen im Client

Mit `KOTH_SHOT=bild.png` zeigt der Client die F3-Anzeige, speichert nach
`KOTH_SHOT_SECONDS` (Standard 7 s) einen Screenshot und beendet sich.
`KOTH_SHOT_YAW`, `KOTH_SHOT_PITCH` und `KOTH_SHOT_FOV` halten den Blick fest.

## Projektstruktur

```
KotH.Shared/   die gesamte Simulation, ohne Grafik: Motor, Physikabfragen, Kampf,
               Tick-Treiber, Rollback; dazu Kollisions-Prefabs ohne Modelle
KotH.Game/     Client: Eingabe, Kamera, Darstellung, HUD, Szene und Materialien
KotH.Linux/    ausführbarer Client (Vulkan, NativeAOT)
KotH.Server/   Konsolen-Host ohne GPU
KotH.Probes/   Messproben gegen die Unity-Referenz
Tools/         Asset-Generatoren, Regressionsvergleich, AOT-Publish
Reference/     eingefrorene Unity-Messungen und der Regressionsbericht
```

Einige Kernpunkte der Architektur:

- **Ein Taktgeber.** `SimulationSystem` tickt alle Körper; kein Körper tickt
  sich selbst. Ein Tick läuft in festen Phasen: Kontakte, Bewegung aller Körper
  auf ihrer eigenen Pose, Trennen, Veröffentlichen, Kampf.
- **Rollback.** `Capture`, `Restore` und `Resimulate` über `WorldSnapshot`. Der
  `MotorState` enthält jedes Motorfeld, die DeterminismProbe prüft das bitgenau.
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
bearbeitet. Für den Umzug wurden sie einmalig aus Skripten erzeugt. Diese Skripte
nicht erneut ausführen: Sie überschreiben jede Änderung aus Game Studio.

| Skript (nur Umzug) | hat erzeugt |
|---|---|
| `Tools/generate_arena.py` | `ArenaCollision.sdprefab` aus den Unity-Dumps, bitgenau |
| `Tools/generate_bodies.py` | `PlayerBody`, `DummyBody` mit allen Tuningwerten |
| `Tools/generate_client.py` | Materialien, Modelle, `MainScene`, Licht |

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
`slide`, `sprint`, `melee`, `step`, `determinism`, `clock`, `stairrun`.

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
und AOT gleich sein; derzeit lautet sie `A4A9BBB937C62890`. Für Peer-to-Peer
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
- `Assets/TextMesh Pro/`: Unitys TextMesh-Pro-Ressourcen, Unity Companion License;
  die enthaltene Schrift Liberation Sans steht unter der SIL Open Font License.
  Das betrifft nur die Unity-Referenz und fällt mit ihr weg.
