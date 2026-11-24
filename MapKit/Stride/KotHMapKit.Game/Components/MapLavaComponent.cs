using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    public enum MapLavaShape { Box, Cylinder }

    /// <summary>A lava pit: the entity's position is its centre, its scale the size in metres (turned
    /// round the vertical only); the lava's surface is its top. Burns in waves as Volcano's magma.
    /// Draw the surface with a mesh of template Lava, collision None. Never exported as geometry.</summary>
    [DataContract("KotH.MapKit.MapLava")]
    [Display("Map lava")]
    [ComponentCategory("KotH map")]
    public sealed class MapLavaComponent : EntityComponent
    {
        /// <userdoc>Box: the scale's box. Cylinder: the ellipse in the scale's X and Z.</userdoc>
        [DataMember(10)] public MapLavaShape Shape { get; set; } = MapLavaShape.Box;
        /// <userdoc>Damage percent of each wave's hit.</userdoc>
        [DataMember(20)] public float DamagePerWave { get; set; } = 10f;
        /// <userdoc>Seconds from one wave to the next.</userdoc>
        [DataMember(30)] public float WaveSeconds { get; set; } = 1f;
        /// <userdoc>Whoever is in the lava with more percent than this respawns.</userdoc>
        [DataMember(40)] public float RespawnAbove { get; set; } = 100f;
    }
}
