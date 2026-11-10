using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    public enum MapVisibility { Private, Friends, Unlisted, Public }

    /// <summary>The map's name and workshop text; exactly one per exported scene.</summary>
    [DataContract("KotH.MapKit.MapInfo")]
    [Display("Map info")]
    [ComponentCategory("KotH map")]
    public sealed class MapInfoComponent : EntityComponent
    {
        /// <userdoc>Folder name of the package: letters, digits, '-' and '_'. Keep it once uploaded.</userdoc>
        [DataMember(10)] public string Name { get; set; } = "MyMap";
        /// <userdoc>The title players see in the menus and the workshop.</userdoc>
        [DataMember(20)] public string Title { get; set; } = "My map";
        [DataMember(30)] public string Author { get; set; } = "";
        [DataMember(40)] public string Description { get; set; } = "";
        /// <userdoc>Who sees the workshop item. Private until you are happy with it.</userdoc>
        [DataMember(50)] public MapVisibility Visibility { get; set; } = MapVisibility.Private;
        /// <userdoc>What changed, for the workshop's change notes.</userdoc>
        [DataMember(60)] public string ChangeNote { get; set; } = "";
    }
}
