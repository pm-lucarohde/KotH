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
| (dieser Commit) | Der einzelne `CapsuleCast` (TryStepUp: Platz eine Stufe höher?) zählte eine Startüberlappung als blockiert. In Unity nachgemessen: Er ignoriert Collider, in denen er startet (`CapsuleCastAll` meldet sie mit Abstand 0). Die Probenzahlen ändern sich nicht. | Port-Prüfer |
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
