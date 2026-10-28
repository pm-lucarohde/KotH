# Stride.Voxels (patched)

From `stride3d/stride`, tag `releases/4.4.0` (a0fdf91), `sources/engine/Stride.Voxels`, MIT (`LICENSE.md`). Kept in
the repo to fix and extend it (Luca's choice), without `DefaultGraphicsCompositorVoxels.sdgfxcomp` and the strong-name
`InternalsVisibleTo`. Every change is marked `KotH patch` in the code.

## Fixes

- `VoxelVolumeProcessor`: volumes live across frames instead of being recreated each frame (GC spikes every 0.4 s).
- A volume with `Voxelize = false` keeps voxels and passes: no voxel views, culling or shadow views every frame.
- `VoxelMarchSetHemisphere6`: the sixth cone nearly doubled the second; it is `(0, 0.895, 0.445)` now.
- Vulkan: `ClearReadWrite(Buffer)` is missing (compute `ClearBuffer`), typed buffers hold at most 2²⁷ elements.

## Additions

- **Dynamic groups:** with `Voxelize` off, only `DynamicGroups` are voxelised each frame and written back per box
  (`BufferToTextureBox`). KotH no longer uses this; the code stays.
- **Sparse storage** (`VoxelStorageSparse`): the whole 256 m volume at 0.125 m only where something is: 1 m cells as
  8³ bricks (9³ with filter border), a page table, an atlas in three levels and a dense 1 m volume with mips (220,000
  bricks). Baked over several frames, each step until two read-back counts agree within 0.1 % (`BakeVoxels` waits for
  `Baked`). About 1.2 ms more than 0.5 m clipmaps at 1440p (RTX 3070), cone `Steps` 7.
- **Sky through the cones:** `LightVoxel.Sky` and `SkyIntensity`; `MarchSky` returns the bent normal and open share,
  lighting diffuse by the skybox SH and specular by the prefiltered cubemap. Replaces a separate skybox light.
- **Capsules for figures:** `LightVoxel.Occluders`, up to 12 groups of 8 capsules; `CapsuleVisibility` darkens the
  diffuse light (about 0.5 ms with 12 figures in a crowd).
- **Lightmap:** `LightVoxel.LightmapSize` (Mount Destiny 4096): meshes with `TEXCOORD1` (`Tools/maps/MapUV`) get the
  voxel light baked once (stage `LightmapBake`, RGBA16F, dilated); `LightmapRenderFeature` sets `KOTH_LIGHTMAP`, then
  only the capsules are live (map GI ~1.3 → ~0.15 ms). A second target (RG16F) bakes the sun from KotH's static shadow
  map (`ForwardRendererVoxels.SunLightmapKey`).
