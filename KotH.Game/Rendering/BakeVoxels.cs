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
        /// <summary>Frames voxelised in full.</summary>
        /// <userdoc>So viele Frames wird die ganze Map voxelisiert.</userdoc>
        public int Frames { get; set; } = 2;

        int done;

        public override void Update()
        {
            if (done > Frames || Entity.Get<VoxelVolumeComponent>() is not { } volume)
                return;
            volume.Voxelize = ++done <= Frames;
        }
    }
}
