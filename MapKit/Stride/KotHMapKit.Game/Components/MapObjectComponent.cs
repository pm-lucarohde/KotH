using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    public enum MapShadowMode { Baked, Live, Off }

    public enum MapCollisionMode { Mesh, None, InvisibleWall }

    public enum MapTemplate { Surface, Terrain, Leaves, Water }

    /// <summary>How the models of this entity and its children are exported (the nearest one up
    /// the tree counts). Without it: Surface, baked shadow, solid.</summary>
    [DataContract("KotH.MapKit.MapObject")]
    [Display("Map object")]
    [ComponentCategory("KotH map")]
    public sealed class MapObjectComponent : EntityComponent
    {
        /// <userdoc>Baked: shadow and bounce light in the lightmap (static, cheapest). Live: shadow
        /// computed every frame (sharper, costs time). Off: casts none.</userdoc>
        [DataMember(10)] public MapShadowMode Shadows { get; set; } = MapShadowMode.Baked;
        /// <userdoc>Mesh: players stand on it exactly as drawn. None: decoration. InvisibleWall:
        /// solid but not drawn.</userdoc>
        [DataMember(20)] public MapCollisionMode Collision { get; set; } = MapCollisionMode.Mesh;
        /// <userdoc>Surface: colour/texture by UVs. Terrain: texture projected from the sides, no
        /// UVs needed. Leaves: alpha cut, both sides. Water: the game's waves.</userdoc>
        [DataMember(30)] public MapTemplate Template { get; set; } = MapTemplate.Surface;
        /// <userdoc>Terrain: metres one texture repeat spans.</userdoc>
        [DataMember(40)] public float TextureScale { get; set; } = 4f;
    }
}
