using KotH.Shared.Mathematics;
using Stride.Core.Mathematics;

namespace KotH.Shared.Simulation
{
    /// <summary>Capsule against capsule as plain arithmetic over value types. No physics
    /// query and no component access, so this moves into a parallel job unchanged once
    /// the body count makes that worthwhile.</summary>
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

        /// <summary>Horizontal overlap of two bodies, for pushing them apart. Only
        /// sideways: landing on someone is meant to slide off, not to carry. Returns
        /// false when the capsules miss each other in height, so a body on a ledge above
        /// another one is left alone.</summary>
        public static bool TryGetSideways(
            in Body a, in Body b, out Vector3 normal, out float overlap)
        {
            normal = UMath.Right;
            overlap = 0f;

            // Heights must share something, or they are not in each other's
            // way at all.
            float aLow = a.center.Y - a.height * 0.5f;
            float aHigh = a.center.Y + a.height * 0.5f;
            float bLow = b.center.Y - b.height * 0.5f;
            float bHigh = b.center.Y + b.height * 0.5f;

            if (aHigh <= bLow || bHigh <= aLow)
                return false;

            Vector3 delta = new Vector3(
                b.center.X - a.center.X, 0f, b.center.Z - a.center.Z);

            float reach = a.radius + b.radius;
            float square = UMath.SqrMagnitude(delta);
            if (square >= reach * reach)
                return false;

            // Axes exactly on top of each other leave no direction to push
            // along. A physics engine used to separate such a pair; nothing does now,
            // so a fixed direction beats staying stuck. It is the same every
            // time, which is what matters for replaying a tick.
            if (square < 0.0001f)
            {
                overlap = reach;
                return true;
            }

            float distance = UMath.Sqrt(square);
            normal = UMath.Div(delta, distance);
            overlap = reach - distance;
            return true;
        }

        /// <summary>Normal from a to b, and whether the two are within range of each
        /// other. A capsule is its inner axis segment grown by the radius, so the normal
        /// stays horizontal while the segments overlap in height and tilts as soon as one
        /// body stands on top of the other.</summary>
        public static bool TryGetNormal(
            in Body a, in Body b, float range, out Vector3 normal)
        {
            normal = Vector3.Zero;

            float aLow = a.center.Y - a.height * 0.5f + a.radius;
            float aHigh = a.center.Y + a.height * 0.5f - a.radius;
            float bLow = b.center.Y - b.height * 0.5f + b.radius;
            float bHigh = b.center.Y + b.height * 0.5f - b.radius;

            float sharedLow = UMath.Max(aLow, bLow);
            float sharedHigh = UMath.Min(aHigh, bHigh);

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
                b.center.X - a.center.X, bY - aY, b.center.Z - a.center.Z);

            float reach = a.radius + b.radius + range;
            float square = UMath.SqrMagnitude(delta);
            if (square > reach * reach)
                return false;

            // Axes exactly on top of each other leave no direction to push
            // along. Inventing one would be arbitrary, and SeparateBodies separates
            // the pair anyway.
            if (square < 0.0001f)
                return false;

            normal = UMath.Div(delta, UMath.Sqrt(square));
            return true;
        }
    }
}
