# Unity-Referenz (Stand b09cb6b, Unity 6000.3.21f1, Editor-Mono)

Eingefroren am 15.10.2026, bevor Unity aus dem Repo verschwindet.

- `runs/run1..3/` – alle acht Bewegungs- und Kampfproben, dreimal, nur die
  Probe-Zeilen aus dem Editor-Log (alle Exit 0).
- `runs/DeterminismProbe.txt` – alle Fälle deterministisch.
- `dump/ground.txt` – Boden-Mesh in Weltkoordinaten samt Wicklung.
- `dump/colliders.txt` – alle Collider der Arena in InstanceID-Reihenfolge.
- `dump/rotations.txt.gz` – Bits von `Quaternion.Euler` und `Quaternion*Vector3`.
- `dump/queries.txt.gz` – PhysX-Antworten auf 6 500 Abfragen je Art.
- `dump/clock.txt` – Unitys Player-Loop über 200 Frames mit festen Schlafzeiten
  (Hänger bis 1 s): echte und gekappte Frame-Zeit, Ticks pro Frame, Alpha,
  Kamera, Auge, Nahebene; Spieler bei Frame 30 um 3 m angehoben. Gemessen nach
  b09cb6b mit einer Einmal-Probe, die Quelle liegt in `.work/unity-dump/ClockProbe.cs`.
- `dump/crouchoffset.txt` – Beleg: `ComputePenetration` legt `center` auf die
  übergebene Pose, die geduckte Hülle wurde 0,45 m zu tief gemessen.

Alles in Unity-Koordinaten (+Z vorn). Die Proben in `KotH.Probes` spiegeln
beim Ein- und Auslesen über `Ref`.

Wichtig für Vergleiche: Der Editor-Mono rundet lokale Variablen auf float,
rechnet aber jeden Ausdruck in double (`RotationProbe` belegt das bitgenau).
Der Port rechnet rein in float, wie es die IL2CPP-Builds taten; Bitgleichheit
mit diesen Logs ist deshalb nicht möglich, nur die Toleranz.
