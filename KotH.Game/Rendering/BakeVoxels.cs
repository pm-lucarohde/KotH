using Stride.Engine;
using Stride.Rendering.Voxels;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Voxelises the map in full once, in the first frames after it is
    /// loaded, then keeps those voxels: map and sun never change. From then
    /// on the volume's dynamic groups (the figures) are voxelised again every
    /// frame, only in the boxes round them (Luca's choice).
    /// </summary>
    /// <userdoc>Voxelisiert die Map einmal nach dem Laden; danach nur noch die Figuren.</userdoc>
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
                volume.Voxelize = !sparse.Baked;
                return;
            }
            if (done > Frames)
                return;
            volume.Voxelize = ++done <= Frames;
        }
    }
}
