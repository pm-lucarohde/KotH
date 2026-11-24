using UnityEngine;

namespace KotH.MapKit
{
    /// <summary>A trampoline: the object stands at its foot on the ground, in the middle. Landing on its
    /// top flies up again, crouching lands, as on The Ladder.</summary>
    [AddComponentMenu("KotH map/Map trampoline")]
    [DisallowMultipleComponent]
    public sealed class MapTrampoline : MonoBehaviour
    {
        [Tooltip("Width of its square top in metres (the game's model is round inside it).")]
        public float diameter = 1.2f;
        [Tooltip("Height of its top over the object in metres.")]
        public float height = 0.6f;
        [Tooltip("Share of the landing speed it gives back upwards.")]
        public float bounce = 0.9f;
        [Tooltip("On: the export adds the game's trampoline and its solid block. Off: build your own, solid up to the top; this object's meshes then export as usual.")]
        public bool model = true;

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 1f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * (height * 0.5f), new Vector3(diameter, height, diameter));
            Gizmos.DrawLine(transform.position + Vector3.up * height, transform.position + Vector3.up * (height + 1.5f));
        }
    }
}
