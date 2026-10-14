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

        // -2 means not looked up yet, -1 that the project has no such layer.
        static int layer = Unresolved;
        const int Unresolved = -2;

        /// <summary>
        /// Resolved from the name, so renumbering the layer cannot leave a
        /// stale index behind. On first use, never in a field initializer:
        /// Unity runs those while the MonoBehaviour is being constructed and
        /// forbids NameToLayer there. -1 means the layer is missing, which
        /// Awake reports instead of silently missing every shot forever.
        /// </summary>
        public static int Layer
        {
            get
            {
                if (layer == Unresolved)
                    layer = LayerMask.NameToLayer(LayerName);

                return layer;
            }
        }

        public static int Mask => Layer < 0 ? 0 : 1 << Layer;

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
