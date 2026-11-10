using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    /// <summary>Where a player starts: feet at the entity's position. Place one per player you
    /// expect (up to 12; fewer are shared). Never exported as geometry.</summary>
    [DataContract("KotH.MapKit.MapSpawn")]
    [Display("Map spawn")]
    [ComponentCategory("KotH map")]
    public sealed class MapSpawnComponent : EntityComponent
    {
        /// <userdoc>Look at the zone; off: look along the entity's forward (-Z).</userdoc>
        [DataMember(10)] public bool FaceZone { get; set; } = true;
    }
}
