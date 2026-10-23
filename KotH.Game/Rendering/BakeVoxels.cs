using KotH.Client.Hud;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Voxels;
using Stride.Rendering.Voxels.VoxelGI;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Voxelises the map in full once, in the first frames after it is
    /// loaded, then keeps those voxels: map and sun never change. The figures
    /// are not voxelised (FigureOccluders hands them over as capsules).
    /// Then has the voxel light baked into its lightmap, if it has one, and
    /// tells the LoadScreen once that is in.
    /// </summary>
    /// <userdoc>Voxelisiert die Map einmal nach dem Laden; die Figuren kommen als Kapseln dazu.</userdoc>
    public sealed class BakeVoxels : SyncScript
    {
        /// <summary>Frames voxelised in full (clipmaps; sparse bricks say when they are done).</summary>
        /// <userdoc>So viele Frames wird die ganze Map voxelisiert.</userdoc>
        public int Frames { get; set; } = 2;

        int done;

        public override void Update()
        {
            if (Entity.Get<VoxelVolumeComponent>() is not { } volume)
                return;
            // Sparse bricks bake over as many frames as they need.
            if (volume.Storage is VoxelStorageSparse sparse)
            {
                volume.Voxelize = !sparse.Baked && StaticShadowDrawn();
                if (sparse.Baked && LightmapBaked())
                    Services.GetService<LoadScreen>()?.Baked();
                return;
            }
            if (done > Frames)
            {
                Services.GetService<LoadScreen>()?.Baked();
                return;
            }
            volume.Voxelize = ++done <= Frames;
        }

        // The voxels are lit with the map's shadow: with a lightmap the map casts only
        // into the static sun shadow, so that has to be complete first.
        bool StaticShadowDrawn()
        {
            if (Entity.Get<LightComponent>()?.Type is not LightVoxel { LightmapSize: > 0 })
                return true;
            return RenderContext.GetShared(Services).Tags.Get(StaticSunShadowRenderer.Key) is { Ready: true };
        }

        // Asks the renderer for the voxel light's lightmap until it is there (none wanted: at once).
        bool LightmapBaked()
        {
            if (Entity.Get<LightComponent>()?.Type is not LightVoxel light || light.LightmapSize <= 0 || light.Lightmap != null)
                return true;
            RenderContext.GetShared(Services).Tags.Set(ForwardRendererVoxels.LightmapRequestKey, light);
            return false;
        }
    }
}
