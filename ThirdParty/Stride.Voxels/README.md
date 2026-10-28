# Stride.Voxels (patched)

Source: `stride3d/stride`, tag `releases/4.4.0` (a0fdf91), `sources/engine/Stride.Voxels`, MIT (`LICENSE.md`). Kept in
the repo so we can fix and extend it (Luca's choice); left out: `DefaultGraphicsCompositorVoxels.sdgfxcomp` and the
strong-name `InternalsVisibleTo`. Every change is marked `KotH patch` in the code.

## Fixes

- `VoxelVolumeProcessor`: volumes live across frames instead of being recreated each frame (GC spikes every 0.4 s).
- `VoxelRenderer.Collect`, `ProcessedVoxelVolume.Prepared`, `VoxelRenderFeature`, `LightVoxelRenderer`: a volume with
  `Voxelize = false` keeps voxels and passes; no voxel views, culling or shadow views every frame any more.
- `VoxelMarchSetHemisphere6`: the sixth cone pointed almost like the second; the ring needs `(0, 0.895, 0.445)`.

## Dynamic groups

`StaticGroups` are voxelised while `Voxelize` is on, then the clipmaps are kept. With `Voxelize` off and `DynamicGroups`
set, each frame restores last frame's boxes, voxelises only the dynamic groups (lit by the camera view) and writes them
back per box (`BufferToTextureBox`). KotH no longer uses this (figures are capsules, below); the code stays.

## Sparse storage (`Voxelization/VoxelStorage/Sparse`)

`VoxelStorageSparse` holds the whole volume (256 m) at 0.125 m only where something is: 1 m cells = bricks of 8³ voxels
(9³ with a +x/+y/+z border for trilinear filtering), page table `pages`, an atlas in three levels (0.125/0.25/0.5 m)
plus a dense 1 m volume with hardware mips; 220,000 bricks.

- **Baking** over several frames (effects compile asynchronously; each step repeats until the read-back count matches
  twice within 0.1 %): mark cells, add the cells below and the filter footprints, assign slots, write fragments in
  slabs ≤ 2²⁷ elements (`BufferToBricks`), then levels and the dense volume (`SparseMip`). `BakeVoxels` waits for `Baked`.
- **Reading** (`VoxelStorageTextureSparseShader`): brick via the filter footprint's lower corner, page 0 = empty; from mip 3 the dense volume.
- Vulkan: `ClearReadWrite(Buffer)` is missing (compute `ClearBuffer`), typed buffers at most 2²⁷ elements.
- Cost (AOT, 1440p, RTX 3070): about 1.2 ms more than 0.5 m clipmaps; cone `Steps` 7. GI at reduced resolution was slower.

## Sky light through the cones

`LightVoxel.Sky` (skybox) and `SkyIntensity`: `VoxelMarchSet.MarchSky` also returns how much sky the cones see (bent
normal, open share). Diffuse: the skybox SH along the bent normal times the share; specular: the prefiltered cubemap
times the share. This replaces a separate skybox light. Voxel views see the sky unoccluded and without specular.

## Capsules for figures

The game fills `LightVoxel.Occluders` each frame: up to 12 groups (figures) of up to 8 capsules behind a bounding
sphere. `LightVoxelShader.CapsuleVisibility` multiplies the diffuse light by what the capsules leave (nearest sphere on
the axis, cosine weighted, faded out by `OccluderFade` radii). About 0.5 ms with 12 figures in a crowd, 0.1–0.2 ms far away.

## Lightmap

`LightVoxel.LightmapSize` (Mount Destiny: 4096): meshes with a second UV set (`TEXCOORD1`, from `Tools/maps/MapUV`)
get the voxel light baked once. `BakeVoxels` asks via `ForwardRendererVoxels.LightmapRequestKey`; the renderer draws
the stage `LightmapBake` (position = UV, both faces) into RGBA16F (light, open sky share), dilates empty texels
(`LightmapDilate`) and releases it after three rounds. `LightmapRenderFeature` sets `KOTH_LIGHTMAP`, then
`LightVoxelShader` reads the lightmap and only the capsules are live. Map GI drops from ~1.3 ms to ~0.15 ms (1440p).
A second render target (RG16F) bakes the sun behind the map from KotH's static shadow map
(`ForwardRendererVoxels.SunLightmapKey`).
