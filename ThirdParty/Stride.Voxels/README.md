# Stride.Voxels (gepatcht)

Quelle: `stride3d/stride`, Tag `releases/4.4.0` (a0fdf91), `sources/engine/Stride.Voxels`, MIT (`LICENSE.md`).
Im Repo, damit wir es reparieren und erweitern können (Lucas Wahl). Weggelassen:
`DefaultGraphicsCompositorVoxels.sdgfxcomp` und die Strong-Name-`InternalsVisibleTo`. Jeder Eingriff ist im
Code mit `KotH patch` markiert; der unveränderte Stand liegt lokal in `.work/stride-src`.

## Fehler behoben

- `VoxelVolumeProcessor`: Volumen leben über Frames weiter, statt jeden Frame neu angelegt zu werden
  (Müll → GC-Spitzen alle 0,4 s); beim Entfernen der Komponente werden sie gelöscht.
- `VoxelRenderer.Collect`, `ProcessedVoxelVolume.Prepared`: Ein Volumen mit `Voxelize = false` behält Voxel und
  Pässe; keine Voxel-Views, kein Culling, keine Schatten-Views mehr (vorher jeden Frame).
- `VoxelRenderFeature`: überspringt solche Volumen. `LightVoxelRenderer`: hält dann keine fremde View mehr für eine
  Voxel-View (alte View-Indizes).

## Dynamische Gruppen (neu)

`VoxelVolumeComponent.StaticGroups` werden voxelisiert, solange `Voxelize` an ist; die Clipmaps werden danach
behalten (`VoxelStorageTextureClipmap.StaticClipMaps`). Ist `Voxelize` aus und `DynamicGroups` gesetzt, läuft jeden
Frame nur noch:

1. `RestoreDynamic`: die Kästen vom letzten Frame aus der Kopie zurückschreiben (`CopyRegion`).
2. Die Voxel-Pässe zeichnen nur `DynamicGroups` (`RenderView.CullingMask`), beleuchtet mit der Kamera-View
   (`LightingView`, deren Schattenkarten vom Vorframe) statt eigener Schattenkarten.
3. `PostProcessDynamic`: `BufferToTextureBox` schreibt je Kasten und Clipmap; leere Zellen behalten die Map, gelesene
   Fragmente werden gelöscht. Kästen = Bounds der Render-Objekte + 2 Voxel, überlappende zusammengelegt.

Die Mipmaps der gröbsten Clipmap bleiben gebacken: Weite Kegel sehen die dynamischen Gruppen nicht.
Braucht `UpdatesPerFrame = AllClipmapsGeometryShader` (alle Clipmaps je Frame) und Attribute ohne Spalten-Modifikatoren.

## Dünn besetzter Speicher (neu, `Voxelization/VoxelStorage/Sparse`)

`VoxelStorageSparse` hält das ganze Volumen (256 m) in 0,125 m, nur wo etwas ist: Zellen von 1 m = Bricks aus 8³
Voxeln (9³ mit Rand +x/+y/+z, damit trilinear in einem Brick bleibt), Seitentabelle `pages` (R32_UInt, Platz + 1),
Atlas in drei Stufen (0,125/0,25/0,5 m) plus ein dichtes 1-m-Volumen mit Hardware-Mips. Neu: 220.000 Bricks (Spiel gesamt ~1,4 GB VRAM).

- **Backen** über mehrere Frames (Effekte kompilieren asynchron; jeder Schritt wiederholt sich, bis die
  zurückgelesene Zahl zweimal auf 0,1 % gleich ist; die Voxelzahl schwankt je Frame): Zellen markieren (`InterlockedOr` in einen Puffer), die Zellen darunter
  anlegen, in die gröbere Filterfüße reichen, Plätze vergeben, Fragmente in Scheiben ≤ 2²⁷ Elemente schreiben
  (`BufferToBricks`), dann Stufen und dichtes Volumen (`SparseMip`). `BakeVoxels` hört auf `Baked`.
- **Lesen** (`VoxelStorageTextureSparseShader`): Brick über die untere Ecke des Filterfußes, Seite 0 = leer; ab
  Mip 3 das dichte Volumen.
- **Dynamisch:** Zellen um die Figuren (+4 Voxel) bekommen je Frame eigene Plätze (bis 1024), `SparseEdit` kopiert
  den gebackenen Brick hinein und setzt die Seiten nach dem Frame zurück. Jede Figur wird in einem eigenen Fenster
  von 64³ Voxeln voxelisiert (ein Fenster über das ganze Volumen kostete ~3 ms, auch ohne Figuren darin). Die Mips
  des dichten Volumens bleiben gebacken. `VoxelRenderer.Collect` nimmt nur Figuren, deren Box, um die Kegelreichweite
  (128 Voxel = 16 m) vergrößert, das Kamera-Frustum schneidet (spart bis ~0,5 ms, Bild gleich).
- Unter Vulkan: `ClearReadWrite(Buffer)` fehlt (Compute `ClearBuffer`), typisierte Puffer höchstens 2²⁷ Elemente.
- Kosten (AOT, 1440p, RTX 3070): rund 1,2 ms mehr als 0,5-m-Clipmaps, davon Figuren ~0,45 ms; Kegel `Steps` 7.
  GI in reduzierter Auflösung (eigener Pass, hochgerechnet) war langsamer: Der Pass kostet pro Pixel etwa das
  Doppelte des Abtastens im Forward-Pass.

## Himmelslicht durch die Kegel (neu)

`LightVoxelShader` kannte keinen Himmel: Ein Kegel ins Freie brachte 0. Jetzt gibt es `LightVoxel.Sky` (Skybox) und
`SkyIntensity`. `VoxelMarchSet.MarchSky` liefert neben dem Licht, wie viel Himmel die Kegel sehen (offene Richtungen
aufsummiert = Bent Normal, offener Anteil). Diffus: die SH der Skybox entlang der Bent Normal, mal Anteil; gespiegelt:
die vorgefilterte Cubemap in Reflexionsrichtung, mal Anteil. Damit ersetzt das Voxel-Licht ein eigenes Skybox-Licht.
In Voxel-Views (Voxelisieren) kommt der Himmel unverdeckt und ohne Spiegelung.

`VoxelMarchSetHemisphere6`: Der sechste Kegel zeigte auf `(0.895, 0.445, 0.445)`, fast wie der zweite; der Ring
braucht `(0, 0.895, 0.445)`.

## Kapseln für Figuren (neu)

KotH voxelisiert die Figuren nicht mehr (`DynamicGroups` leer; der Weg oben bleibt im Code). Stattdessen füllt das
Spiel je Frame `LightVoxel.Occluders`: bis 12 Gruppen (Figuren) zu je bis 8 Kapseln (Enden, Radius), jede Gruppe
hinter einer Kugel, die ihre Reichweite umfasst. `LightVoxelShader.CapsuleVisibility` multipliziert das diffuse Licht
(Kegel und Himmel) mit dem, was die Kapseln übrig lassen: je Kapsel die Kugel auf der Achse, die dem Punkt am nächsten
ist, kosinusgewichtet ohne Horizontschnitt, ausgeblendet bis `OccluderFade` (4) Radien; ein Punkt in einer Kapsel zählt
wie auf ihrer Oberfläche. Nicht in Voxel-Views. Genauere Formeln (Horizontschnitt nach Quilez, Integral entlang der
Achse) sahen kaum anders aus und kosteten mehr. Kosten (JIT, 1440p, 12 Figuren × 6 Kapseln im Gedränge) ~0,5 ms,
Figuren weit weg ~0,1–0,2 ms; die Voxel-Figuren kosteten mit Bots im Bild ~2 ms.

## Lightmap (neu)

`LightVoxel.LightmapSize` (Neu: 4096): Meshes mit zweitem UV-Set (`TEXCOORD1`, für Berg und Meer aus `Tools/maps/MapUV`)
bekommen das Voxel-Licht einmal gebacken. `BakeVoxels` fragt nach dem Voxel-Backen über die Tags des
`RenderContext` (`ForwardRendererVoxels.LightmapRequestKey`); der Renderer zeichnet die Stufe `LightmapBake`
(`LightmapBakeEffect`: Position = UV, beide Seiten, `IsFrontFace` fest, sonst kippt Stride die Normale gespiegelter
Karten) mit einer View, die wie die Kamera beleuchtet ist, ohne Kapseln, nach RGBA16F (Licht, offener Himmelsanteil),
füllt leere Texel in 4 Schritten (`LightmapDilate`) und gibt sie frei, wenn alle Bake-Effekte kompiliert und 3 Runden
gezeichnet sind. `LightmapRenderFeature` setzt über `ExtensionPostVertexStageShader` Position bzw. `KOTH_LIGHTMAP`:
Dann liest `LightVoxelShader` die Lightmap, die Kapseln kommen live dazu. Bild wie live (1–2/255), GI auf der Map
statt ~1,3 ms noch ~0,15 ms (1440p); Figuren und Blätter rechnen weiter live; das Meer ist mitgebacken (flache Normale beim Backen, Wellen live). Ein Pass in 50/80 % Auflösung
für den Rest war wieder langsamer als voll.

Sonne: Die Bake-Stufe schreibt per zweitem Render-Target (RG16F, Grün = beschrieben) auch die Sonne hinter der Map
(`bakedSun`, aus KotHs statischer Schattenkarte, `CascadeShadowReceiver` unter `KOTH_LIGHTMAP_BAKE`); frei über
`ForwardRendererVoxels.SunLightmapKey`. `LightmapDilate` füllt beide (Marker: Alpha oder Grün < 0).
