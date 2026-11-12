using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Match
{
    /// <summary>Magma round the capture zone's centre (Volcano's crater), on the zone's entity:
    /// whoever's feet are under its surface takes a hit of damage percent every wave (with shake
    /// and recoil), and once over a limit respawns like one who fell (Luca). No collider.</summary>
    [DataContract("KotH.MagmaPool")]
    [Display("Magma pool")]
    [ComponentCategory("KotH")]
    public sealed class MagmaPool : EntityComponent
    {
        /// <userdoc>Horizontal reach from the zone's centre, in metres.</userdoc>
        [Display("Radius (m)", "Magma")] public float Radius = 6.5f;

        /// <userdoc>Height of the magma's surface, in metres. Whoever's feet are below it burns.</userdoc>
        [Display("Surface (m)", "Magma")] public float Surface = 0f;

        /// <userdoc>Damage percent of each wave's hit in the magma.</userdoc>
        [Display("Damage per wave (%)", "Magma")] public float DamagePerWave = 10f;

        /// <userdoc>Seconds from one wave to the next, on the round's clock (the same beat for everyone).</userdoc>
        [Display("Wave every (s)", "Magma")] public float WaveSeconds = 1f;

        /// <userdoc>Whoever is in the magma with more percent than this respawns at a spawn point, as after a fall. Percent and points stay.</userdoc>
        [Display("Respawn above (%)", "Magma")] public float RespawnAbove = 100f;
    }
}
