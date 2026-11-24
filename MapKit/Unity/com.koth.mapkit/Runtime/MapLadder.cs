using UnityEngine;

namespace KotH.MapKit
{
    /// <summary>A ladder on a wall: the object stands at its foot on the wall's face, its forward (blue
    /// arrow) looking out of the wall. Pushing towards the wall inside it climbs, as on The Ladder.</summary>
    [AddComponentMenu("KotH map/Map ladder")]
    [DisallowMultipleComponent]
    public sealed class MapLadder : MonoBehaviour
    {
        [Tooltip("Width along the wall in metres, centred on the object.")]
        public float width = 1f;
        [Tooltip("Height from the foot in metres: at the top the feet step onto the ledge.")]
        public float height = 3f;
        [Tooltip("On: the export adds the game's ladder (two rails, rungs). Off: build your own; this object's meshes then export as usual.")]
        public bool model = true;

        // The strip: rails at its sides, a rung every 0.3 m, an arrow out of the wall.
        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.9f, 0.6f, 0.2f, 1f);
            Vector3 foot = transform.position + Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized * 0.05f;
            Vector3 side = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized * (width * 0.5f), up = Vector3.up * height;
            Gizmos.DrawLine(foot - side, foot - side + up);
            Gizmos.DrawLine(foot + side, foot + side + up);
            for (float y = 0.15f; y < height - 0.05f; y += 0.3f)
                Gizmos.DrawLine(foot - side + Vector3.up * y, foot + side + Vector3.up * y);
            Gizmos.DrawLine(foot + Vector3.up, foot + Vector3.up + transform.forward * 0.6f);
        }
    }
}
