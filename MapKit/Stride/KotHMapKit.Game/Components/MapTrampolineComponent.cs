using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    /// <summary>A trampoline: the entity stands at its foot on the ground, in the middle. Landing on its
    /// top flies up again, crouching lands, as on The Ladder.</summary>
    [DataContract("KotH.MapKit.MapTrampoline")]
    [Display("Map trampoline")]
    [ComponentCategory("KotH map")]
    public sealed class MapTrampolineComponent : EntityComponent
    {
        /// <userdoc>Width of its square top in metres (the game's model is round inside it).</userdoc>
        [DataMember(10)] public float Diameter { get; set; } = 1.2f;
        /// <userdoc>Height of its top over the entity in metres.</userdoc>
        [DataMember(20)] public float Height { get; set; } = 0.6f;
        /// <userdoc>Share of the landing speed it gives back upwards.</userdoc>
        [DataMember(30)] public float Bounce { get; set; } = 0.9f;
        /// <userdoc>On: the export adds the game's trampoline and its solid block. Off: draw your own,
        /// solid up to the top; this entity's models then export as usual.</userdoc>
        [DataMember(40)] public bool Model { get; set; } = true;
    }
}
