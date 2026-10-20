# Regression Unity → Stride (Etappe 6, 15.10.2026, Stand 3e36f07)

Verglichen werden alle acht Bewegungs- und Kampfproben sowie die DeterminismProbe
gegen die eingefrorene Unity-Referenz in `Reference/unity/runs/run1..3`.
Werkzeuge:
- `Tools/regress.py`: Zahlen mit 5 mm Toleranz, Zeilen pro Tick eingeschlossen.
- `Tools/verdict.py`: Urteile.

Läufe: `KotH.Probes <name>`, jede Probe dreimal.

## Ergebnis

- **Urteile:** 94 von 94 gleich mit Unity.
- **Determinismus:**
  - Die DeterminismProbe meldet alle Fälle bitgenau.
  - Ihre Ausgabe ist zeilengleich mit Unity.
  - Die Mutationstests schlagen an (fehlendes Restore-Feld, verstecktes Motorfeld,
    halber Tick sichtbar, fehlendes Damage-Restore).
  - Drei Läufe jeder Probe sind byte-gleich, also auch prozessübergreifend
    deterministisch.

| Probe | Fälle | in 5 mm | Anzeige­rundung | nur 1 % | darüber | Anmerkung |
|---|---|---|---|---|---|---|
| SlopeJump | 6 | 6 | | | | zeilengleich |
| Crest | 18 | 17 | | 1 | | geduckt 0°: oben bei Tick 196 statt 195 |
| Impact | 28 | 24 | 4 | | | Halbwerte wie 2,875 als „2,87“ |
| JumpHeight | 18 | 18 | | | | |
| Slide | 41 | 41 | | | | byte-gleich |
| Sprint | 111 | 111 | | | | byte-gleich |
| Melee | 5 (+1) | 5 | | | | „== Schrift“ fehlt, kommt mit dem Client-HUD |
| Step | 140 | 129 | | 1 | 2 | siehe unten |
| Determinism | – | – | | | | zeilengleich |

## Verbleibende Abweichungen und Ursachen

1. **Anzeigerundung.** Mono formatiert floats erst auf 7 Stellen und rundet dann.
   .NET rundet den exakten Wert. Darum wird 2,875 (intern 2,87499…) zu „2,87“
   statt „2,88“. Ebenso gibt .NET eine negative Null als „-0,00“ aus. Die Werte
   selbst sind gleich.
2. **Rechengenauigkeit.** Unitys Editor-Mono rechnet Ausdrücke in double, belegt
   durch die `RotationProbe`. Der Port rechnet in float wie die IL2CPP-Builds.
   Daher kommt Crest geduckt einen Tick später oben an (Tick 196 statt 195,
   Gipfel 5,034 statt 5,033 m).
3. **Step, Ducken bzw. Aufstehen direkt nach einem Teleport.**
   - Ausgangslage: Die Proben setzen die Füße exakt auf y = 0.
   - Ob ein Overlap dieses bloße Berühren meldet, entscheiden PhysX und Bepu im
     Gleitkommarauschen jeweils anders.
   - Erst dann greift Unitys absichtlich nachgebaute Eigenheit: Die geduckte Hülle
     wird in `ComputePenetration` um `CenterY` zu tief gemessen.
   - Folge: Der Stoß nach oben kommt in einem anderen Tick (Steps20 geduckt: t001
     statt t004; Steps40 ruhig: t000 statt t001).
   - Nur bei „+ Steps40 ruhig“ wirkt das bis ins Ergebnis: Endlage x −14,1 / z 19,0
     statt −14,0 / 19,3. Gipfel 2,440 m, Endhöhe und Urteil sind gleich.
   - Ein Filter für Berührungen in `OverlapCapsule` macht es schlechter: 9 statt
     2 Fälle über der Toleranz, und ein Fall im PhysX-Abfrageraster geht verloren.
     PhysX meldet Berührung also im Overlap mit. Der Filter wurde verworfen.
   - Im Spiel tritt der Fall praktisch nicht auf: Ein gelandeter Körper ruht eine
     Hautdicke über dem Boden, nicht exakt darauf.
4. **Step, Bodenwinkel in Wackelfällen.** 1,0° statt 0,9° (t032) und 19,7° statt
   19,6° (t029), angezeigt mit einer Nachkommastelle. Folge von Punkt 2.

## Während der Regression gefundene und behobene Fehler

| Commit | Fehler | Gefunden durch |
|---|---|---|
| 43c90b0 | Bepus Compound-Sweep sah bei maxDistance < 1 nur maxDistance² weit (Strahl `sweep·maximumT` mit schon skaliertem `sweep`). Fix: Weg als Geschwindigkeit über T = 1. | Crest, Impact, JumpHeight, Step, Sprint, Slide |
| d586828 | Bepu meldet bloßes Berühren mit Rauschtiefe; `Depenetrate` hob den Körper um die Haut (4 cm). Schwelle 1e-5 m, PhysX' kleinste echte Tiefe im Dump: 6,4e-5. | Step, Sprint, Melee |
| 3e36f07 | `StaticComponent.Teleport` schreibt die Entity auf Pose − Schwerpunkt zurück; geduckt stand die Transform bis 0,45 m zu hoch. | Determinism |
| d1999a5 | Der einzelne `CapsuleCast` (TryStepUp: Platz eine Stufe höher?) zählte eine Startüberlappung als blockiert. In Unity nachgemessen: Er ignoriert Collider, in denen er startet (`CapsuleCastAll` meldet sie mit Abstand 0). Die Probenzahlen ändern sich nicht. | Port-Prüfer |
| d9d3b23 | Penetrationsrichtung zeigte zur Bepu-Seite statt zur PhysX-Seite. | WorldQueryProbe |

Physikabfragen gegen PhysX (`KotH.Probes worldquery`, 6 500 Abfragen je Art):
- Alle Treffermengen sind gleich, auch bei den kurzen Sweeps (d + 1 cm).
- Abstände: p99 unter 5 µm.
- Penetrationen: höchstens 0,4 mm und 0,17°.

## Unabhängige Port-Prüfung

Ein Prüf-Agent hat KotH.Shared Zeile für Zeile mit `Assets/Scripts/Shared` verglichen:
Formeln, Reihenfolgen, Schwellen, Früh-Returns, Spiegelung, alle 35 Motor- und die
Kampf-Tuningwerte samt Unity-Prefab-Overrides. Ergebnis: kein verhaltensändernder Befund
außer dem einzelnen CapsuleCast (oben). Offen zum Merken:
- Trigger kennt WorldQueries nicht; die Capture-Zone braucht einen eigenen, aus WorldMask
  ausgenommenen Layer.
- Treffer gleicher Collider werden nur gegen Startüberlappungen entdoppelt;
  zusammengesetzte Geometrie mit mehreren Kindern wäre neu zu prüfen.
- Überlappungen werden nach Registrierreihenfolge statt Unitys Instanz-ID summiert
  (nur letzte Bits).

## NativeAOT (Etappe 8)

Probes, Server und Client als NativeAOT (`Tools/publish_aot.sh`, Box `aot`), alle mit
dem Befehlssatz **x86-64-v2** (Lucas Entscheidung, `Directory.Build.props`):
- Alle acht Bewegungs- und Kampfproben sind unter AOT byte-gleich mit JIT, ebenso
  `worldquery` und `clock`.
- DeterminismProbe: Hashkette über 1001 Ticks `10BFD5B7A436F3D8` unter JIT und AOT
  (`KotH.Probes determinism hashes`, gibt auch den Befehlssatz aus).
- Server: 2000 Ticks, Endzustand des Dummys bitgleich mit JIT.

Die Gleitkommaergebnisse hängen am Befehlssatz: AVX2/FMA-Code rundet anders als
SSE-Code (mit AVX2 lautet die Kette `290BCEB905C5638C`, drei Step-Winkel kippen in der
letzten Anzeigestelle). Ein JIT-Build auf einer AVX2-CPU rechnet deshalb nur mit
`DOTNET_EnableAVX=0` wie die AOT-Builds. Die runtimeconfig nimmt diese Einstellung nicht
an, nur die Umgebung. `.work/tools/stride_runs.sh` setzt sie. Für Peer-to-Peer heißt das:
Alle Peers brauchen dasselbe AOT-Binary.

Regression gegen Unity unter v2 (JIT mit `DOTNET_EnableAVX=0`, drei Läufe byte-gleich):
94/94 Urteile gleich, 352 von 367 Werten in 5 mm, 4 Anzeigerundung. Die übrigen Fälle
sind die oben beschriebenen: Crest geduckt einen Tick später, Steps40 ruhig, Step-Ticks
und ein Bodenwinkel in der letzten Stelle (t018: 2,1 statt 2,0°; t032 und t029 stimmen
unter v2 mit Unity überein).

Verbleibende AOT-Warnungen (8) liegen alle in Fremdcode: Silk.NET (Pfadsuche über
`Assembly.Location`/`DependencyContext`, IL3000/IL3002), Stride.Graphics (nur macOS)
und Stride.BepuPhysics (IL3053). KotH selbst erzeugt keine.

## Gewollte Änderungen nach dem Umzug (16.10.2026)

Luca hat drei Fehler gemeldet, die nach Etappe 8 behoben wurden. Zwei davon
steckten schon im Unity-Original:

1. **Gehaltene Leertaste löste den Doppelsprung aus** (`b0e26d0`, Fehler aus dem
   Umzug). Strides `IsKeyPressed` meldet die Tastenwiederholung des Systems mit,
   Unitys `GetKeyDown` nicht. `KeyPresses` zählt nur echte Drücke.
2. **Gleiten direkt aus dem Sprint-Sprung** (`b0e26d0`, neues Verhalten): mit
   gehaltener Ducktaste gelandet, startet der Slide im Landetick. SlideProbe hat
   dafür drei Fälle ohne Unity-Gegenstück.
3. **Hängenbleiben beim Hochsprinten der Treppen** (Fehler aus Unity). Nach einem
   Stufenaufstieg schwebt der Körper eine Hautdicke über dem Absatz und gilt erst
   zwei Ticks später wieder als am Boden. Im Sprint kommt die nächste Kante alle
   2,8 Ticks, fällt also irgendwann in dieses Fenster, und der Körper prallt ab.
   Jetzt darf der Aufstieg auch in diesen zwei Ticks greifen (`stepSettleTicks`).
   Neue Probe `stairrun`: vorher 154 von 2916 Sprintläufen gestockt, jetzt keiner.

Alle anderen Proben sind dabei byte-gleich geblieben, unter JIT und AOT. Die
Hashkette der DeterminismProbe lautet jetzt `A4A9BBB937C62890`. Sie ändert sich, weil
`MotorState` ein neues Feld hat; das Verhalten bleibt in allen Fällen gleich.

## Gewollte Änderung in Etappe 10 (20.10.2026)

4. **Körper wurden durch den Boden geschoben** (Fehler aus Unity, gefunden im
   Lasttest des Servers mit 64 Bots: 47 Durchfälle in 4 400 Ticks). Am Fuß von
   Ramp20 und Ramp35 bleibt unter der schrägen Box ein Keil, in den man geduckt
   hineinkriecht. `SeparateBodies` schob dort mit `Motor.Nudge` ungeprüft weiter,
   der Körper steckte danach in der Rampe, und deren Tiefenkorrektur drückte ihn
   durch den einseitigen Boden. `Nudge` fährt die Verschiebung jetzt mit
   `CapsuleSweep.Clear` ab und hält eine Hautdicke vor der Welt an; ist nichts im
   Weg, bleibt die Verschiebung bitgleich. Neue Probe `rampfoot`: ein Körper
   allein sowie ein Pulk aus sechs, der sich unter den Rampen schiebt. Mit dem
   alten `Nudge` sinkt der Pulk in allen 8 Durchgängen ein, jetzt in keinem.

5. **Geduckt an der Seitenkante einer Rampe durch den Boden** (Fehler aus Unity
   oder aus dem Port, ohne PhysX-Referenz für `ComputePenetration` nicht zu
   trennen). Die Unterseite der Rampe drückt den Körper ein paar Zentimeter nach
   unten; danach misst die Tiefenkorrektur den Boden mit dem übernommenen
   Duck-Versatz (Kapsel 0,45 m zu tief), und Bepu liefert die waagerechte
   Diagonalkante eines Bodendreiecks statt „nach oben“. `Depenetrate` fährt
   einen Schub nach unten jetzt per Sweep ab und lässt ihn am Boden entlang
   gleiten; andere Schübe bleiben ungeprüft wie in Unity, Boxen lösen sie im
   nächsten Durchgang selbst. `rampfoot` prüft alle vier Seitenkanten mit
   5 940 Läufen: vorher 286 eingesunken, jetzt keiner. Lasttest mit 64 Bots
   über 40 000 Ticks: kein Durchfall mehr.

Alle Bewegungsproben sind nach beiden Änderungen byte-gleich zum Stand davor,
unter JIT und AOT; die Hashkette bleibt `A4A9BBB937C62890`.

## Gewollte Änderung nach dem Umzug (20.10.2026, von Luca gemeldet)

6. **Geduckt an Steps30 zappelte der Körper an der Stufe** (Fehler aus Unity).
   Geduckt ist die Stufenhöhe 0,25 m, die Kanten von Steps30 und Steps40 liegen
   darüber, aber unter der Mitte der unteren Kugel (0,4 m). Die schräge
   Kantennormale hob den Körper bei jedem Tick auf die Kante, die Haltekraft von
   −2 m/s wurde daran in Rückwärtsfahrt umgelenkt: Er schwebte 1,2 cm hoch und
   wurde alle 7 Ticks 1,4 cm zurückgestoßen. Jetzt gilt eine solche Kante für
   einen Körper am Boden, der waagerecht in sie hineinläuft, als senkrechte Wand
   (`CapsuleSweep.Move`, Parameter `grounded`). Von oben getroffen, etwa beim
   Absteigen über eine Seitenkante, bleibt alles wie bisher. Stehend greift die
   Regel nie: Die Stufenhöhe 0,45 m liegt über dem Kugelradius.
   - Neue Probe `crouchedge`: Steps30, 40 und 50 geduckt, gerade und schräg bis
     60°, je 45 Läufe. Vorher zappelten 20 Läufe, jetzt keiner.
   - StepProbe: „− Steps30 geduckt“ endet 3,1 statt 3,8 cm über dem Start, „−
     Steps30 geduckt Ecke“ 0 statt 3,8 cm (Gipfel 0 statt 4,5 cm). Die Differenz
     ist das Reiten auf der Kante; beide liegen jetzt über der Toleranz, die
     Urteile bleiben gleich. „= Kante geduckt“ weicht um 1 mm ab. Alle anderen
     Proben byte-gleich, Hashkette unverändert.

## Neue Mechanik: Rückstoß nach Schadensprozent (20.10.2026, Lucas Vorgabe)

7. **Ein Treffer stößt den Getroffenen weg** (in Unity gab es keinen Rückstoß).
   Es zählt das Prozent vor dem Treffer: 0 % stößt nicht, 100 % ergeben ohne
   Gegensteuern auf ebenem Boden 12 m waagerecht und 1,2 m Höhe, linear bis
   1000 % (120 m / 12 m); zuerst waren es 2 m / 0,2 m, Luca hat zweimal
   verdoppelt und dann um 50 % erhöht. Einstellbar in `MeleeAttack` („Weite/Höhe bei 100 %“).
   Die Richtung ist waagerecht weg vom Angreifer.
   - `PlayerMotor.Knock` setzt die Startgeschwindigkeit
     `v0 = a·dt/2 + √((a·dt/2)² + 2·a·d)`: waagerecht mit der
     Luftbeschleunigung (7,5 m/s²) als Bremse, senkrecht mit der Schwerkraft.
     Der Term `a·dt/2` gleicht aus, dass der Motor vor dem Bewegen bremst.
   - Der Stoß ist ein eigener Anteil `knock` der Geschwindigkeit, der sich
     unabhängig von der Eingabe abbaut; das eigene Laufen kommt obendrauf.
     Wer gegen den Stoß läuft, nimmt ihm genau sein Lauftempo ab. In der Luft
     beschleunigt das eigene Laufen dabei nur mit der Luftkontrolle. Was eine
     Wand an Tempo nimmt, geht zuerst vom Stoß ab (`ClipKnock`). Der
     Kantenschutz beim Ducken ruht, solange ein Stoß wirkt.
   - `knock` steht in `MotorState`; die DeterminismProbe prüft Rollback und
     Vollständigkeit weiter bitgenau.
   - Neue Probe `knockback`: 0 / 1,2 / 50 / 100 / 250 / 500 / 1000 % treffen
     Weite und Höhe auf 2 mm, nur 500 % landet 7 mm zu kurz (100 %: 12,000 m /
     1,201 m, 1000 %: 120,000 m / 12,001 m); dazu Gegen- und Mitlaufen bei
     100 %: 1,596 m (Stoß + eigenes Laufen = 1,601 m) und 22,404 m (22,404 m).
   - MeleeProbe stellt den Rückstoß ab, sie misst Reichweite und Trefferzahl
     gegen Unity. Alle Bewegungsproben sind byte-gleich zum Stand davor.
   - Die Hashkette der DeterminismProbe lautet jetzt `AFC01679A2071001`, unter
     JIT und AOT gleich (die Probe schlägt zu). Lasttest mit 64 Bots:
     Endzustand `075339B60C1BB468` unter JIT und AOT, etwa 1 ms je Tick.
   - Im Spiel stehen drei Dummys mit 0 %, 50 % und 100 % Startschaden
     nebeneinander; Server und Proben behalten ihren einen.
