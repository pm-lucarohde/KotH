using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Match
{
    /// <summary>A lava pit anywhere on the map (custom maps' [[lava]]), on the capture zone's entity like
    /// Volcano's magma pool and with its waves: feet inside the box or cylinder, under its top, take a
    /// hit every wave and respawn over the limit. The bots keep off it. No collider.</summary>
    [DataContract("KotH.LavaArea")]
    [Display("Lava area")]
    [ComponentCategory("KotH")]
    [AllowMultipleComponents]
    public sealed class LavaArea : EntityComponent
    {
        /// <userdoc>Middle of the area in world metres; its top (the lava's surface) is half the height above.</userdoc>
        [Display("Centre (m)", "Lava")] public Vector3 Center;

        /// <userdoc>Width (X), height (Y) and depth (Z) in metres, before the turn.</userdoc>
        [Display("Size (m)", "Lava")] public Vector3 Size = new Vector3(4f, 1f, 4f);

        /// <userdoc>Turn round the vertical in degrees (Stride's: 90 turns -Z to -X).</userdoc>
        [Display("Yaw (°)", "Lava")] public float Yaw;

        /// <userdoc>An upright cylinder (the ellipse in width and depth) instead of a box.</userdoc>
        [Display("Cylinder", "Lava")] public bool Cylinder;

        /// <userdoc>Damage percent of each wave's hit in the lava.</userdoc>
        [Display("Damage per wave (%)", "Lava")] public float DamagePerWave = 10f;

        /// <userdoc>Seconds from one wave to the next, on the round's clock.</userdoc>
        [Display("Wave every (s)", "Lava")] public float WaveSeconds = 1f;

        /// <userdoc>Whoever is in the lava with more percent than this respawns, as after a fall.</userdoc>
        [Display("Respawn above (%)", "Lava")] public float RespawnAbove = 100f;
    }
}
