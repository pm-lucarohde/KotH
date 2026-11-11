using UnityEngine;

namespace KotH.MapKit
{
    public enum MapZoneShape { Box, Cylinder, Polygon }

    /// <summary>The hill's zone: the object's position is its centre and its scale its size in
    /// metres (drawn in yellow). Exactly one per map; never exported as geometry.</summary>
    [AddComponentMenu("KotH map/Map zone")]
    [DisallowMultipleComponent]
    public sealed class MapZone : MonoBehaviour
    {
        [Tooltip("Box: the scale's box. Cylinder: the ellipse in the scale's X and Z. Polygon: one child object per corner, in order; the scale's Y is the height.")]
        public MapZoneShape shape = MapZoneShape.Box;
        [Tooltip("Seconds alone inside to take the zone.")]
        public float captureSeconds = 3f;
        [Tooltip("Points that win the round (one per second held).")]
        public int winPoints = 100;
        public float timeLimitMinutes = 20f;
        [Tooltip("Players below this height (metres) respawn.")]
        public float respawnBelow = -5f;
        [Tooltip("Respawns on a ring round the zone, from this radius ... (0: the spawn points)")]
        public float ringInner;
        [Tooltip("... to this one.")]
        public float ringOuter;
        public float ringSpacing = 6f;
        public float winPauseSeconds = 15f;
        public float countdownSeconds = 60f;
        public float joinSeconds = 60f;

        /// <summary>Adds a corner after the last one, to drag into place.</summary>
        [ContextMenu("Add corner")]
        void AddCorner()
        {
            var corner = new GameObject("Corner " + (transform.childCount + 1)).transform;
            corner.SetParent(transform, false);
            int n = transform.childCount;
            corner.position = n > 1 ? transform.GetChild(n - 2).position + Vector3.right : transform.position;
            shape = MapZoneShape.Polygon;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 1f);
            Vector3 s = transform.lossyScale, at = transform.position;
            float low = at.y - Mathf.Abs(s.y) * 0.5f, high = at.y + Mathf.Abs(s.y) * 0.5f;
            if (shape == MapZoneShape.Polygon && transform.childCount >= 2)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    Vector3 a = transform.GetChild(i).position, b = transform.GetChild((i + 1) % transform.childCount).position;
                    Prism(new Vector3(a.x, low, a.z), new Vector3(b.x, low, b.z), high - low, true);
                    Gizmos.DrawSphere(new Vector3(a.x, low, a.z), 0.15f);
                }
                return;
            }
            if (shape == MapZoneShape.Cylinder)
            {
                for (int i = 0; i < 48; i++)
                {
                    float u = i * Mathf.PI / 24f, v = (i + 1) * Mathf.PI / 24f;
                    Prism(new Vector3(at.x + Mathf.Cos(u) * s.x * 0.5f, low, at.z + Mathf.Sin(u) * s.z * 0.5f),
                          new Vector3(at.x + Mathf.Cos(v) * s.x * 0.5f, low, at.z + Mathf.Sin(v) * s.z * 0.5f), high - low, i % 6 == 0);
                }
                return;
            }
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 0.25f);
            Gizmos.DrawCube(Vector3.zero, Vector3.one);
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }

        // An edge at the floor and the top, with a post at its start if asked.
        static void Prism(Vector3 a, Vector3 b, float height, bool post)
        {
            Vector3 up = Vector3.up * height;
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(a + up, b + up);
            if (post)
                Gizmos.DrawLine(a, a + up);
        }
    }
}
