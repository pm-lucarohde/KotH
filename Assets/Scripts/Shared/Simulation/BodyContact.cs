using UnityEngine;

namespace KotH.Shared.Simulation
{
    /// <summary>
    /// Capsule against capsule as plain arithmetic over value types. No PhysX
    /// query and no component access, so this moves into a Burst job unchanged
    /// once the body count makes that worthwhile.
    /// </summary>
    public static class BodyContact
    {
        /// <summary>One upright capsule at the moment of the test.</summary>
        public struct Body
        {
            public Vector3 center;
            public Vector3 velocity;
            public float height;
            public float radius;
        }

        /// <summary>
        /// Horizontal overlap of two bodies, for pushing them apart. Only
        /// sideways: landing on someone is meant to slide off, not to carry.
        /// Returns false when the capsules miss each other in height, so a
        /// body on a ledge above another one is left alone.
        /// </summary>
        public static bool TryGetSideways(
            in Body a, in Body b, out Vector3 normal, out float overlap)
        {
            normal = Vector3.right;
            overlap = 0f;

            // Heights must share something, or they are not in each other's
            // way at all.
            float aLow = a.center.y - a.height * 0.5f;
            float aHigh = a.center.y + a.height * 0.5f;
            float bLow = b.center.y - b.height * 0.5f;
            float bHigh = b.center.y + b.height * 0.5f;

            if (aHigh <= bLow || bHigh <= aLow)
                return false;

            Vector3 delta = new Vector3(
                b.center.x - a.center.x, 0f, b.center.z - a.center.z);

            float reach = a.radius + b.radius;
            float square = delta.sqrMagnitude;
            if (square >= reach * reach)
                return false;

            // Axes exactly on top of each other leave no direction to push
            // along. PhysX used to separate such a pair; now nothing does,
            // so a fixed direction beats staying stuck. It is the same every
            // time, which is what matters for replaying a tick.
            if (square < 0.0001f)
            {
                overlap = reach;
                return true;
            }

            float distance = Mathf.Sqrt(square);
            normal = delta / distance;
            overlap = reach - distance;
            return true;
        }

        /// <summary>
        /// Normal from a to b, and whether the two are within range of each
        /// other. A capsule is its inner axis segment grown by the radius, so
        /// the normal stays horizontal while the segments overlap in height
        /// and tilts as soon as one body stands on top of the other.
        /// </summary>
        public static bool TryGetNormal(
            in Body a, in Body b, float range, out Vector3 normal)
        {
            normal = Vector3.zero;

            float aLow = a.center.y - a.height * 0.5f + a.radius;
            float aHigh = a.center.y + a.height * 0.5f - a.radius;
            float bLow = b.center.y - b.height * 0.5f + b.radius;
            float bHigh = b.center.y + b.height * 0.5f - b.radius;

            float sharedLow = Mathf.Max(aLow, bLow);
            float sharedHigh = Mathf.Min(aHigh, bHigh);

            float aY, bY;
            if (sharedLow <= sharedHigh)
            {
                // The segments share height, so the closest points sit at the
                // same level and the normal comes out purely horizontal.
                aY = bY = (sharedLow + sharedHigh) * 0.5f;
            }
            else if (aHigh < bLow)
            {
                aY = aHigh;
                bY = bLow;
            }
            else
            {
                aY = aLow;
                bY = bHigh;
            }

            Vector3 delta = new Vector3(
                b.center.x - a.center.x, bY - aY, b.center.z - a.center.z);

            float reach = a.radius + b.radius + range;
            float square = delta.sqrMagnitude;
            if (square > reach * reach)
                return false;

            // Axes exactly on top of each other leave no direction to push
            // along. Inventing one would be arbitrary, and PhysX separates
            // the pair anyway.
            if (square < 0.0001f)
                return false;

            normal = delta / Mathf.Sqrt(square);
            return true;
        }
    }
}
