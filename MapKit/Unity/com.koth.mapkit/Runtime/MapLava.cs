using UnityEngine;

namespace KotH.MapKit
{
    public enum MapLavaShape { Box, Cylinder }

    /// <summary>A lava pit: the object's position is its centre, its scale the size in metres (turned round
    /// the vertical only; drawn in red); the lava's surface is its top. Burns in waves as Volcano's
    /// magma. Draw the surface with a mesh of template Lava, collision None. Never exported as geometry.</summary>
    [AddComponentMenu("KotH map/Map lava")]
    [DisallowMultipleComponent]
    public sealed class MapLava : MonoBehaviour
    {
        [Tooltip("Box: the scale's box. Cylinder: the ellipse in the scale's X and Z.")]
        public MapLavaShape shape = MapLavaShape.Box;
        [Tooltip("Damage percent of each wave's hit.")]
        public float damagePerWave = 10f;
        [Tooltip("Seconds from one wave to the next.")]
        public float waveSeconds = 1f;
        [Tooltip("Whoever is in the lava with more percent than this respawns.")]
        public float respawnAbove = 100f;

        void OnDrawGizmos()
        {
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f), transform.lossyScale);
            Gizmos.color = new Color(1f, 0.25f, 0.05f, 1f);
            if (shape == MapLavaShape.Box)
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                return;
            }
            for (int i = 0; i < 48; i++)
            {
                float u = i * Mathf.PI / 24f, v = (i + 1) * Mathf.PI / 24f;
                foreach (float y in new[] { -0.5f, 0.5f })
                    Gizmos.DrawLine(new Vector3(Mathf.Cos(u) * 0.5f, y, Mathf.Sin(u) * 0.5f), new Vector3(Mathf.Cos(v) * 0.5f, y, Mathf.Sin(v) * 0.5f));
            }
        }
    }
}
