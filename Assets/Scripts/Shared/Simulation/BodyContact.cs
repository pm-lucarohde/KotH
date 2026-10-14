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
