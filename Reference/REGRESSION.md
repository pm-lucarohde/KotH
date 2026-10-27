# Regression Unity → Stride (Etappe 6, 15.10.2026, Stand 3e36f07)

Verglichen werden alle acht Bewegungs- und Kampfproben sowie die DeterminismProbe
gegen die eingefrorene Unity-Referenz in `Reference/unity/runs/run1..3`.
Werkzeuge:
- `Tools/Regress.cs`: Zahlen mit 5 mm Toleranz, Zeilen pro Tick eingeschlossen.
- `Tools/Verdict.cs`: Urteile.

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
   Es zählt das Prozent vor dem Treffer: 0 % stößt nicht weg, 100 % ergeben ohne
   Gegensteuern auf ebenem Boden 12 m waagerecht und 1,2 m Höhe, linear bis
   1000 % (120 m / 12 m); zuerst waren es 2 m / 0,2 m, Luca hat zweimal
   verdoppelt und dann um 50 % erhöht. Einstellbar in `MeleeAttack` („Weite/Höhe bei 100 %“).
   Die Richtung ist waagerecht weg vom Angreifer. Die Höhe gilt mindestens
   wie bei 1 % (Lucas Vorgabe, „Höhe mindestens wie bei %“), auch bei 0 %:
   ein Hüpfer von 1,2 cm, waagerecht bleibt dabei alles unberührt.
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
   - **Tempo beider beim Treffer** (Lucas Vorgabe): Das waagerechte Tempo auf
     der Stoßlinie zählt mit, das des Schlägers zum Getroffenen hin und das
     des Getroffenen von ihm weg; ab 10 % vor dem Treffer zur Hälfte, linear
     steigend bis voll ab 20 %, darunter gar nicht. Der gezählte Teil vom
     Laufen des Getroffenen geht in den Stoß über, der Rest bleibt sein
     eigenes Laufen. Laufen beide gleich schnell aufeinander zu, fliegt er wie
     im Stand. Der Stoß zeigt nie zum Schläger hin. Einstellbar: „Tempo zählt ab
     %“, „Tempoanteil am Anfang“, „Tempo voll ab %“.
   - **Senkrecht** (Lucas Vorgabe): Steigt oder fällt der Getroffene in der
     Luft, bleibt dieses Tempo, und der Stoß nach oben kommt voll obendrauf,
     ohne Staffel; am Boden zählt es nicht. Das senkrechte Tempo des Schlägers
     zählt nicht. Ein laufender Sprung des Getroffenen wird nach dem Treffer
     beim Loslassen nicht mehr gekürzt, sonst fräße das den Stoß nach oben.
   - `knock` steht in `MotorState`; die DeterminismProbe prüft Rollback und
     Vollständigkeit weiter bitgenau.
   - Neue Probe `knockback`: 0 / 1,2 / 50 / 100 / 250 / 500 / 1000 % treffen
     Weite und Höhe auf 2 mm, nur 500 % landet 7 mm zu kurz (100 %: 12,000 m /
     1,201 m, 1000 %: 120,000 m / 12,001 m); dazu Gegen- und Mitlaufen bei
     100 %: 1,596 m (Stoß + eigenes Laufen = 1,601 m) und 22,404 m (22,404 m).
     Der Boden reicht nur bis x = 25 m: 500 % und 1000 % fliegen über den Rand
     und werden in der Luft gemessen, wo der Stoß genauso abklingt; die Probe
     weist das aus. Schlägertempo bei 100 %: gehend heran 23,043 m, sprintend
     32,389 m, rückwärts weg 4,528 m; bei 10 / 15 / 20 % gehend 3,111 /
     5,494 / 8,326 m (im Stand 1,2 / 1,8 / 2,4 m); unter 10 % und bei 0 %
     wirkt es nicht. Getroffener bei 100 %: geht heran 4,528 m, geht weg
     23,043 m; beide gehen aufeinander zu 12,000 m (bei 20 % 2,401 m), hält
     der Getroffene danach weiter auf den Schläger zu, 2,468 m. Senkrecht bei
     100 % (Höhe über dem Trefferpunkt, im Stand 1,2 m): im Steigen mit
     5,5 m/s 3,864 m, im kurzen Sprung mit Loslassen nach dem Treffer 4,511 m
     (ohne das Ende der Kürzung 0,620 m), im Fallen mit 2,5 m/s 0,491 m;
     waagerecht jeweils 12,000 m. Hashkette und Lasttest berühren diesen Teil
     nicht.
   - MeleeProbe stellt den Rückstoß ab, sie misst Reichweite und Trefferzahl
     gegen Unity. Alle Bewegungsproben sind byte-gleich zum Stand davor.
   - Die Hashkette der DeterminismProbe lautet jetzt `2645BFE8429DA550`, unter
     JIT und AOT gleich (die Probe schlägt zu, seit dem Hüpfer bei 0 % schon
     beim ersten Treffer). Lasttest mit 64 Bots: Endzustand `ACA28FF778F7B0AE`
     unter JIT und AOT, etwa 1 ms je Tick.
   - Im Spiel stehen drei Dummys mit 0 %, 50 % und 100 % Startschaden
     nebeneinander; Server und Proben behalten ihren einen.

## Trefferfläche folgt der Pilzfigur (20.10.2026, Lucas Vorgabe)

8. **Drei Formen statt einer Kapsel** (in Unity: Kapsel mit Radius 0,40 m über
   die volle Höhe). Die Pilzfigur ist an Beinen schmaler und an der Kappe breiter,
   also trifft jetzt, was man sieht: drei aufrechte Zylinder in `PlayerBody` und
   `DummyBody`, Beine 0–0,4 m mit r 0,25, Rumpf 0,4–1,1 m mit r 0,405, Kappe
   1,1–1,8 m mit r 0,45 (über dem Fuß; Rumpf und Kappe hat Luca nach dem ersten
   Stand mit 0,45 / 0,50 um 10 % schmaler gewünscht). Der Rumpf ist in
   Blickrichtung noch einmal 10 % flacher: zwei Zylinder mit r 0,3645, seitlich um
   ±0,0405 versetzt, zusammen 0,81 m breit und 0,73 m tief. Die Hülle dreht mit
   dem Körper (`SyncHull` teleportiert mit seiner Drehung). Nur die Trefferfläche: Bewegung und
   Körperstoß bleiben bei `BodyRadius` 0,40 m.
   - Ducken staucht jede Form zu den Füßen hin, wie das Modell
     (`PlayerMotor.ApplyHeight`); ihr Stand kommt aus dem Prefab.
   - MeleeProbe misst bewusst anders als Unity: Der Dummy steht um 90° gedreht,
     der Strahl trifft ihn also von vorn; Radius in der Mitte 0,362 statt 0,400,
     letzter Treffer bei 3,780 m statt 3,820 m (Strahl 3,483 m), Breite der
     Hülle 0,900 (die Kappe). Takt, Deckel und seitlicher Versatz unverändert.
   - Alle anderen Proben sind byte-gleich zum Stand davor, `knockback` auch.
     Die Hashkette bleibt `2645BFE8429DA550`, der Lasttest mit 64 Bots auch bei
     `ACA28FF778F7B0AE` (mit r 0,45 / 0,50 trafen die Bots andere Schläge:
     `04074E8D84E233AF`), jeweils unter JIT und AOT gleich.

## Third-Person-Kamera (20.10.2026, Lucas Vorgabe)

9. **Mausrad zurück schaltet in Third-Person** (in Unity gab es nur die
   Ego-Perspektive). Die Kamera kreist frei; die Figur läuft in Kamerarichtung.
   Das Fadenkreuz bleibt in der Mitte; ein Schlag zielt vom Auge auf den ersten
   Punkt der Mittellinie, höchstens bis die Linie die Reichweite ums Auge verlässt.
   Das rechnet nur `PlayerInputReader` in die bestehenden Felder
   `MoveInput.Yaw/Pitch/Move` um, die Simulation bleibt gleich:
   Hashkette `2645BFE8429DA550`, Lasttest `ACA28FF778F7B0AE`, `clock` wie Unity.
   - Neue Probe `thirdperson` ohne Unity-Referenz: Kamera hinter jeder freien
     Stelle der Arena (Gitter 0,5 m, stehend und geduckt, Gier in 15°-Schritten,
     8 Nickwinkel, 3,77 Mio. Blicke). Die Kamerakugel darf nichts überlappen
     (1 cm Toleranz für gestreifte Flächen), beide Wegstücke müssen frei sein, die
     Nahebene muss in der Kugel liegen.

## Rückstoß prallt von Wänden ab (20.10.2026, von Luca gemeldet)

10. **Ein schräg gegen die Wand gestoßener Körper flog nach der Landung zurück zur
    Wand.** `ClipKnock` kürzte den Rückstoß nur, seine Richtung blieb: Der Rest
    zeigte weiter in die Wand, der eigene Anteil (Tempo minus Rückstoß) dafür von ihr
    weg. In der Luft sah das wie ein schwacher Abprall aus, am Boden bremste die
    Reibung den eigenen Anteil, und der Rest zog den Körper zurück (bis 1,6 m/s).
    Gerade gegen die Wand blieb ein Körper dagegen kleben.
    - Jetzt verliert der Rückstoß seinen Teil in die Wand, 40 % davon kommen als
      Abprall zurück (`PlayerMotor.KnockWallBounce`, Lucas Wahl); der Teil an der Wand
      entlang bleibt. Das eigene Laufen prallt nie ab.
    - Neue Probe `wallknock` ohne Unity-Referenz: Plateauwand, 0°, 15°, 30° und 45°,
      100 bis 1000 %; nach dem Abprall darf sich der Körper nie mehr zur Wand bewegen.
    - Ohne Rückstoß kehrt `ClipKnock` sofort zurück wie bisher: `knockback`
      byte-gleich, Hashkette `2645BFE8429DA550` und Lasttest `ACA28FF778F7B0AE`
      unverändert, unter JIT und AOT gleich.

## Capture-Zone (20.10.2026, Lucas Vorgabe)

11. **Neue Spielregel ohne Unity-Vorbild.** `HillMatch` (`KotH.Shared/Match`) tickt am
    Ende von `Step()`, nach dem Kampf, auf den veröffentlichten Posen. Die Zone ist die
    Komponente `CaptureZone` im Arena-Prefab, eine Box über dem Plateau (12 × 3,5 × 12 m),
    abgefragt mit `UMath` statt über die Physik; der offene Punkt „Trigger kennt
    WorldQueries nicht“ (oben) entfällt damit.
    - Regeln in Ticks: 120 Ticks allein bis zum ersten Punkte-Tick, 40 Ticks je Punkt,
      Sieg bei 150 Punkten oder nach 48 000 Ticks (Bester gewinnt, Gleichstand
      unentschieden), 200 Ticks Pause, dann `ResetToSpawn` aller Körper.
    - `BodyState` hat zwei Felder mehr (`ScoreTicks`, `AloneTicks`), `WorldSnapshot`
      hält den `MatchState`. `Capture` und `Restore` sind dafür Instanzmethoden.
    - Die Bewegungsproben laufen ohne Runde (`HeadlessWorld.StartMatch` ist ein
      Opt-in), ihr Messpfad ist unberührt; `knockback` und `wallknock` byte-gleich.
    - Die DeterminismProbe spielt die Runde mit (63 Ticks allein auf dem Plateau). Ihre
      Hashkette ist jetzt `880D0C64389C53E3`, weil der Zustand zwei Felder und die
      Runde mehr hat; alle Rollback-Fälle bitgleich, JIT und AOT gleich.
    - Lasttest `ACA28FF778F7B0AE` unverändert (100 s erreichen keinen Sieg).
    - Neue Probe `capture`: Aufwärmen, Punkte, umkämpft, Neustart des Aufwärmens,
      Zonengrenzen, Sprung, Sieg, Pause, Spawn-Zustand bitgleich, Zeitlimit mit
      Sieger und Unentschieden, Rollback samt Runde. JIT und AOT byte-gleich.

## Netcode N1: kein statischer Simulationszustand (20.10.2026)

12. **Körperliste, Tickzähler, Kontaktdiagnose und Probe-Haken gehören jetzt dem
    `SimulationSystem`**, nicht mehr der Klasse. Damit laufen mehrere Welten in einem
    Prozess, die Grundlage für Netcode-Proben mit Host und Mitspielern.
    - Die acht Bewegungsproben von `stride_runs.sh` sind byte-gleich mit dem Stand davor
      (`407a261`), dazu `knockback` und `wallknock`. Das Urteil bleibt „95, davon 94
      gleich“; das eine ist der gewollte Gleitfall „Start bei der Landung“ ohne Unity-Urteil.
    - Hashkette `880D0C64389C53E3` und Lasttest `ACA28FF778F7B0AE` unverändert, JIT und AOT.
    - Neuer Fall der DeterminismProbe: Eine zweite Welt im selben Prozess rechnet die
      Aufnahme im Wechsel mit der ersten nach, beide 1000 Ticks bitgleich.

## Netcode N2: andere glibc (20.10.2026)

13. **Die Simulation rechnet unter glibc 2.35 (Ubuntu 22.04) und 2.43 (Fedora 44) gleich.**
    - Lückenlos über alle endlichen Float-Werte geprüft: `sinf`, `cosf`, `sin`, `cos`; über
      den Definitionsbereich `acos` (float und double); `atan2` an 200 Mio. Stichproben.
      Alles bitgleich (`.work/tools/libmcheck`). Eigene Winkelfunktionen sind nicht nötig.
    - JIT-Läufe in der Box `sf3d`: Hashkette `880D0C64389C53E3` und Lasttest
      `ACA28FF778F7B0AE` wie auf dem Host; `knockback` und `wallknock` gleich bis aufs
      Dezimalzeichen (die Box hat kein ICU, siehe unten).
    - Anders rechnen unter 2.35 nur `acosf`, `asinf` und `atan2f`; glibc 2.43 hat sie neu
      versioniert. Die Simulation nutzt keine davon, Bepu und Stride.BepuPhysics auch nicht.
      Der Lasttest-Bot lenkte mit `MathF.Atan2` und erzeugte deshalb unter 2.35 andere
      Eingaben; jetzt nutzt er `UMath.Atan2` (double), der Endzustand bleibt gleich.
    - **Verteilung, Lucas Entscheidung:** Fedora 44 bleibt die Build-Box, die AOT-Binaries
      verlangen deshalb glibc ≥ 2.43 (`acosf@GLIBC_2.43` u. a.) und starten unter Ubuntu
      22.04 nicht. `libicu` wird vorausgesetzt, ohne bricht .NET beim deutschen Zahlenformat ab.

## Netcode N3: Zustand als Bytes, Rollback über Beitritt und Verlassen (20.10.2026)

14. **Ein Snapshot geht bitgenau über die Leitung.** `StateCodec` (`KotH.Shared/Net`)
    schreibt je Körper `NetId`, Zustand und Spawn-Zustand als rohe Bytes, dazu die Runde
    mit IDs statt Referenzen. `MotorState` und `BodyState` sind dafür mit `Pack = 1`
    gepackt, ohne Füllbytes (202 Bytes je Körper). Der FNV-Hash der Bytes ist die
    Prüfsumme der Peers.
    - `Restore` stellt jetzt auch die Mitgliedschaft und die Reihenfolge der Körperliste her:
      Körper, die der Snapshot nicht kennt, werden geparkt (aus dem Tick, Hülle weit unter
      der Arena, Platz in der Abfrage-Ordnung bleibt). `Leave` und `Rejoin` dazu. Die
      Reihenfolge zählt, weil Float-Summen über drei berührende Körper an ihr hängen.
    - DeterminismProbe: Der Fall „Körperliste umgedreht“ dreht die Liste jetzt nach dem
      `Restore` um. Hashkette `880D0C64389C53E3` und Lasttest `ACA28FF778F7B0AE`
      unverändert, `knockback` und `wallknock` byte-gleich.
    - Neue Probe `netstate`: Übertragung in eine zweite Welt mit 600 Ticks bitgleich weiter
      über ein Rundenende, Rollback über Beitritt und Verlassen bitgleich, frischer Körper
      in zwei Welten gleich, falscher Kopf, unbekannte ID und gekipptes Byte erkannt.
      JIT und AOT byte-gleich.

## Netcode N4: Rollback-Kern mit Host, Vorhersage und Reparatur (20.10.2026)

15. **Elf Peers rechnen über ein verlustbehaftetes Netz bitgleich.** Neu in
    `KotH.Shared/Net`: `NetProtocol`, `Transport` (`MemoryNetwork`), `NetWorld`,
    `HostSession`, `PeerSession`. Die Simulation selbst ist unverändert; nur
    `HeadlessWorld` gibt seinen `Builder` heraus.
    - Hashkette `880D0C64389C53E3`, Lasttest `ACA28FF778F7B0AE`; `capture`, `netstate`,
      `knockback`, `wallknock`, `melee`, `impact`, `clock` byte-gleich, JIT und AOT.
    - Neue Probe `netplay`: Host plus zehn Peers in einem Prozess (sechs früh, vier ab
      20 s), 15–75 ms einfach mit 15 % Jitter und 5 % Verlust, einer mit 140 ± 70 ms und
      10 % Verlust. Einer geht nach 30 s, einer wird über die Bytes verfälscht, ein
      falscher Build klopft an. 64 s Spiel: 518 Prüfsummen ohne Eingriff gleich, die
      Verfälschung erkannt und repariert, rund 17 000 Rollbacks, tiefster 15 Ticks,
      am Ende alle bitgleich mit dem Host. Verkehr vom Host 14–51 KiB/s je Peer.
    - JIT und AOT liefern dieselben Zähler und dieselbe `BuildId`; JIT ohne
      `DOTNET_EnableAVX=0` hätte eine andere und würde abgewiesen.

## Netcode N5: Lobby, Countdown und Startplätze (20.10.2026)

16. **Die Runde wartet im Mehrspieler auf 4 Spieler und zählt 60 s herunter.**
    `MatchState` hat statt `Over` eine Phase (`Running` = 0 und `Over` = 1 wie zuvor,
    neu `Waiting` und `Countdown`), `PhaseTicks` statt `PauseTicks` und `MinPlayers`.
    Offline bleibt `MinPlayers` 0 und alles wie bisher. `StateCodec` Version 2, die
    Runde hat 22 statt 21 Bytes. `CaptureZone` hat `CountdownSeconds` und `JoinSeconds`
    (je 60). Im Arena-Prefab liegen `PlayerSpawn0` bis `PlayerSpawn11` in zwei Reihen
    à sechs bei z 4,5 und 6,5 mit Blick zur Zone; ein Halbkreis um den Spawn stieß an
    `Ramp20`, `CrouchTest` und die Rampe.
    - Hashkette `880D0C64389C53E3`, Lasttest `ACA28FF778F7B0AE`; `knockback`,
      `wallknock`, `melee` byte-gleich, `netstate` bis auf die Zustandslänge
      (1674 statt 1673 Bytes), der alte Teil von `capture` byte-gleich. JIT und AOT gleich.
    - `capture` neu: warten mit 3 Spielern und einem Dummy, Countdown beim vierten,
      Abbruch beim Gehen, Neustart beim Zurückkommen, Start am Startplatz, Beitrittsfenster,
      nach der Runde wieder warten; alle 12 Startplätze frei und auf dem Boden.
    - `netplay` neu: Runde mit Lobby (Countdown für die Probe 10 s, Fenster 20 s),
      Countdown ab Tick 28, Runde ab Tick 428, Beitritt bei 45 s abgelehnt (`Closed`).

## Netcode: Drehung in der Vorhersage (20.10.2026)

17. **Fremde Spieler drehen sich in der Vorhersage weiter.** Statt die letzte bestätigte
    Blickrichtung zu halten, schreibt `PeerSession.Guess` die Drehung der letzten zwei
    bestätigten Eingaben fort, höchstens 8 Ticks (`TurnAhead`, Lucas Wunsch). Die
    Simulation ist unberührt, nur die Vermutung ändert sich.
    - Die `netplay`-Bots drehen jetzt gleichmäßig wie eine Maus (vorher ±60° auf einen
      Schlag plus Zittern), die Zähler der Probe sind deshalb neu.
    - Gemessen über 64 s (Korrektur = Sprung der am weitesten versetzten fremden Figur
      je Rollback): halten 14,0 cm / 3,87°, 4 Ticks 13,0 cm / 3,64°, **8 Ticks
      12,8 cm / 3,49°**, 15 Ticks 12,8 cm / 3,62°. Die Zahl der Rollbacks bleibt gleich
      (rund 17 200): Eine geschätzte Drehung trifft den echten Wert nie aufs Bit.
    - Glätten der Korrektur in der Darstellung will Luca nicht.
    - JIT und AOT gleich, `netstate` besteht.

## Netcode N6: UDP, Startargumente, fremde Spieler (20.10.2026)

18. **Mehrspieler über echtes UDP.** `LiteNetTransport` mit LiteNetLib 2.1.4, Client mit
    `--host`/`--join`/`--name`/`--port`/`--min-players`/`--display`, Server mit
    `--host`/`--join`/`--bot`. Fremde Spieler als Pilzfigur mit Name und Prozent, F3 mit
    Ping, Vorlauf, Rollback und Warten. Die Simulation ist unberührt.
    - Hashkette `880D0C64389C53E3`, Lasttest `ACA28FF778F7B0AE`; `capture`, `netstate`,
      `knockback`, `wallknock`, `melee` byte-gleich mit N5, JIT und AOT.
    - **Datagramme höchstens 1200 Bytes.** Vorher packte der Host bis zu 24 bestätigte
      Ticks in eine Nachricht, bei 12 Spielern bis 6 KB, zu groß für UDP im Internet.
      Ein erster Versuch mit Limit, aber immer ab dem ältesten unquittierten Tick, ließ
      den Peer mit 280 ms Ping endlos zurückfallen (Endtick 1880 statt 2520, 2471
      Eingaben ersetzt). Jetzt vom neuesten Tick rückwärts, Wiederholung ab dem ältesten
      erst bei überfälliger Quittung (Host schätzt den Ping aus den Quittungen).
      Verkehr vom Host je Peer 12–39 KiB/s statt bis 51.
    - **Uhr steht beim Warten.** Ein Host-Client schickt sein Hallo während des Ladens
      und läuft erst 1,5 s später: Seine Uhr stand 60 Ticks vor, die Korrektur sprang auf
      veralteten Messwerten immer wieder (Vorlauf 15, ständig Warten). Jetzt wartet die
      Korrektur nach einem Sprung auf frische Werte, und ein wartender Peer hält seine
      Uhr an. Neuer Fall in `netplay` („Ladepause“), ohne beide Korrekturen scheitert er
      (558/750-mal gewartet), mit ihnen 130-mal im Anlauf, danach 0. Nebenbei wartet der
      Peer mit der schlechten Leitung nur noch 127- statt 2082-mal.
    - **Eingabeprüfung:** Der Host klemmt NaN, Unendlich, Bewegung über 1, Blick über 89°
      und mehr als 4 Tastendrücke je Tick; 100 000 gültige Eingaben bleiben bitgleich.
    - **Sieg nur bestätigt:** Das HUD zeigt „Du gewinnst!“ erst aus dem bestätigten Stand.
    - UDP-Rauchtest auf localhost (`.work/udp/smoke.sh`): Host + 3 Bots, der vierte nach
      30 s, Countdown 60 s, Runde ab 91 s, Beitritt bei 160 s abgelehnt (`Closed`), alle
      Prüfsummen gleich, 0 Desyncs, 0 Eingaben ersetzt. JIT-Host mit AOT-Gast ebenso gleich.

## Einnehmen: allein nur die ersten 3 s (21.10.2026)

19. **Gewollte Regeländerung (Luca).** Allein muss man nur beim Einnehmen sein. Wer die
    Zone eingenommen hat, bleibt `Holder` und punktet, solange er drin ist, auch wenn
    andere dazukommen; geht er hinaus, ist sie frei, und wer dann allein drin steht,
    nimmt sie selbst in 3 s ein. Kommt während des Einnehmens jemand dazu, ist sie
    weiter umkämpft, und das Einnehmen beginnt von vorn.
    - Probe `capture` neu im Fall „Teilen“: A hält mit B darin (+201 Punkte-Ticks in
      201 Ticks, B 0), A draußen: B erst nach 120 Ticks, beim Einnehmen umkämpft: keiner.
    - Hashkette `880D0C64389C53E3` unverändert (ihr Szenario hat nie zwei in der Zone
      nach dem Einnehmen); `determinism`, `netstate`, `netplay`, `melee`, `bots` bestehen.
    - Bots über 12 Seeds: Punkte je Runde 4,8 statt 0,8, sonst gleich (Sprünge 13,3 statt
      13,1, Schläge 52,9 statt 52,8, Treffer 16,4).
