using UnityEngine;

namespace KotH.Shared.Combat
{
    /// <summary>
    /// Marks a collider as something a shot may hit. No health, no damage:
    /// it only turns a Collider back into the body it belongs to. Belongs
    /// on the GameObject that carries the collider.
    /// </summary>
    public sealed class Hittable : MonoBehaviour
    {
        public const string LayerName = "Hittable";

        // Resolved from the name, so renumbering the layer cannot leave a
        // stale index behind. -1 means the project has no such layer, which
        // Awake reports instead of silently missing every shot forever.
        public static readonly int Layer = LayerMask.NameToLayer(LayerName);
        public static readonly int Mask = Layer < 0 ? 0 : 1 << Layer;

        void Awake()
        {
            if (Layer < 0)
                Debug.LogWarning("Layer " + LayerName + " fehlt im Projekt.", this);
            else if (gameObject.layer != Layer)
                Debug.LogWarning("Hittable liegt nicht auf " + LayerName + ".", this);

            // CharacterController derives from Collider, so it counts.
            if (GetComponent<Collider>() == null)
                Debug.LogWarning("Hittable ohne Collider wird nie getroffen.", this);
        }
    }
}
