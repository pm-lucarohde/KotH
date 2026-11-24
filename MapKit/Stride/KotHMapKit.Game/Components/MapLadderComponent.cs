using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    /// <summary>A ladder on a wall: the entity stands at its foot on the wall's face, its forward (-Z)
    /// looking out of the wall. Pushing towards the wall inside it climbs, as on The Ladder.</summary>
    [DataContract("KotH.MapKit.MapLadder")]
    [Display("Map ladder")]
    [ComponentCategory("KotH map")]
    public sealed class MapLadderComponent : EntityComponent
    {
        /// <userdoc>Width along the wall in metres, centred on the entity.</userdoc>
        [DataMember(10)] public float Width { get; set; } = 1f;
        /// <userdoc>Height from the foot in metres: at the top the feet step onto the ledge.</userdoc>
        [DataMember(20)] public float Height { get; set; } = 3f;
        /// <userdoc>On: the export adds the game's ladder (two rails, rungs). Off: draw your own;
        /// this entity's models then export as usual.</userdoc>
        [DataMember(30)] public bool Model { get; set; } = true;
    }
}
