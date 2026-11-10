using UnityEngine;

namespace KotH.MapKit
{
    public enum MapShadowMode { Baked, Live, Off }

    public enum MapCollisionMode { Mesh, None, InvisibleWall }

    public enum MapTemplate { Surface, Terrain, Leaves, Water }

    /// <summary>How the meshes of this object and its children are exported (the nearest one up
    /// the hierarchy counts). Without it: Surface, baked shadow, solid.</summary>
    [AddComponentMenu("KotH map/Map object")]
    [DisallowMultipleComponent]
    public sealed class MapObject : MonoBehaviour
    {
        [Tooltip("Baked: shadow and bounce light in the lightmap (static, cheapest). Live: shadow computed " +
                 "every frame (sharper, costs time). Off: casts none.")]
        public MapShadowMode shadows = MapShadowMode.Baked;
        [Tooltip("Mesh: players stand on it exactly as drawn. None: decoration. InvisibleWall: solid but not drawn.")]
        public MapCollisionMode collision = MapCollisionMode.Mesh;
        [Tooltip("Surface: colour/texture by UVs. Terrain: texture projected from the sides, no UVs needed. " +
                 "Leaves: alpha cut, both sides. Water: the game's waves.")]
        public MapTemplate template = MapTemplate.Surface;
        [Tooltip("Terrain: metres one texture repeat spans.")]
        public float textureScale = 4f;
    }
}
