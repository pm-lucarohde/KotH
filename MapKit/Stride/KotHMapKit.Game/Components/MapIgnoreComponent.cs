using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    /// <summary>The entity and its children stay in the kit: helpers such as the scale figure
    /// and the 256 m bounds.</summary>
    [DataContract("KotH.MapKit.MapIgnore")]
    [Display("Map ignore")]
    [ComponentCategory("KotH map")]
    public sealed class MapIgnoreComponent : EntityComponent
    {
    }
}
