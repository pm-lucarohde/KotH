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
