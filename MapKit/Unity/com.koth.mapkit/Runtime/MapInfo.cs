using UnityEngine;

namespace KotH.MapKit
{
    public enum MapVisibility { Private, Friends, Unlisted, Public }

    /// <summary>The map's name and workshop text; exactly one per exported scene. Selected, it
    /// shows the map's bounds: green within the 256 m the light covers, red beyond.</summary>
    [AddComponentMenu("KotH map/Map info")]
    [DisallowMultipleComponent]
    public sealed class MapInfo : MonoBehaviour
    {
        [Tooltip("Folder name of the package: letters, digits, '-' and '_'. Keep it once uploaded.")]
        public string mapName = "MyMap";
        [Tooltip("The title players see in the menus and the workshop.")]
        public string title = "My map";
        public string author = "";
        [TextArea(2, 6)] public string description = "";
        [Tooltip("Who sees the workshop item. Private until you are happy with it.")]
        public MapVisibility visibility = MapVisibility.Private;
        [Tooltip("What changed, for the workshop's change notes.")]
        public string changeNote = "";

        void OnDrawGizmosSelected()
        {
            bool any = false;
            var all = new Bounds();
            foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!r.enabled || r.GetComponentInParent<MapIgnore>() != null)
                    continue;
                if (any)
                    all.Encapsulate(r.bounds);
                else
                    all = r.bounds;
                any = true;
            }
            if (!any)
                return;
            Vector3 s = all.size;
            Gizmos.color = Mathf.Max(s.x, Mathf.Max(s.y, s.z)) > 256f ? Color.red : Color.green;
            Gizmos.DrawWireCube(all.center, s);
        }
    }
}
