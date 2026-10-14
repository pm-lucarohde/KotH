using UnityEngine;

namespace KotH.Shared.Combat
{
    /// <summary>
    /// How far a body is charged up. Not health: it only counts upwards and
    /// stops at a cap, and it is meant to scale how far a hit sends the body
    /// flying later on. Belongs on the body that gets hit.
    /// </summary>
    public sealed class DamagePercent : MonoBehaviour
    {
        [Header("Schadenswert")]
        [Tooltip("Obergrenze. Darüber nimmt der Körper nichts mehr auf.")]
        [SerializeField] float maxPercent = 1000f;

        float percent;

        public float Percent => percent;
        public float MaxPercent => maxPercent;
        public bool IsCapped => percent >= maxPercent;

        /// <summary>
        /// Charges the body up. Returns whether the value actually moved -
        /// at the cap the answer is false, and that is the place where
        /// "nothing happens after this" will turn into something else.
        /// </summary>
        public bool Add(float amount)
        {
            if (amount <= 0f || percent >= maxPercent)
                return false;

            percent = Mathf.Min(percent + amount, maxPercent);
            return true;
        }

        /// <summary>Back to zero, for a round restart or a probe.</summary>
        public void Reset()
        {
            percent = 0f;
        }
    }
}
