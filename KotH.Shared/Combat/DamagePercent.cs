using KotH.Shared.Mathematics;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Combat
{
    /// <summary>
    /// How far a body is charged up. Not health: it only counts upwards and
    /// stops at a cap, and it is meant to scale how far a hit sends the body
    /// flying later on. Belongs on the body that gets hit.
    /// </summary>
    [DataContract("KotH.DamagePercent")]
    [Display("Schadenswert")]
    [ComponentCategory("KotH")]
    public sealed class DamagePercent : EntityComponent
    {
        /// <userdoc>Obergrenze. Darüber nimmt der Körper nichts mehr auf.</userdoc>
        [Display("Obergrenze", "Schadenswert")] public float MaxPercent = 1000f;

        float percent;

        [DataMemberIgnore] public float Percent => percent;
        [DataMemberIgnore] public bool IsCapped => percent >= MaxPercent;

        /// <summary>
        /// Charges the body up. Returns whether the value actually moved -
        /// at the cap the answer is false, and that is the place where
        /// "nothing happens after this" will turn into something else.
        /// </summary>
        public bool Add(float amount)
        {
            if (amount <= 0f || percent >= MaxPercent)
                return false;

            percent = UMath.Min(percent + amount, MaxPercent);
            return true;
        }

        /// <summary>
        /// Sets the value as a rollback captured it. Not a hit: no cap
        /// check, no notion of change, just the state as it was.
        /// </summary>
        public void Restore(float value)
        {
            percent = value;
        }

        /// <summary>Back to zero, for a round restart or a probe.</summary>
        public void Reset()
        {
            percent = 0f;
        }
    }
}
