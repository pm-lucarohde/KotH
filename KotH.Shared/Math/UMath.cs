using System;
using Stride.Core.Mathematics;

namespace KotH.Shared.Mathematics
{
    /// <summary>Unity's managed Mathf, Vector2, Vector3 and Quaternion formulas, kept
    /// operation for operation. The simulation was tuned and measured against these exact
    /// roundings; Stride's own helpers round differently, e.g. Normalize multiplies by
    /// the inverse and keeps tiny vectors.</summary>
    public static class UMath
    {
        // Unity's Mathf.PI is the float pi, so its Deg2Rad is not the double one.
        public const float PI = 3.14159274f;
        public const float Deg2Rad = PI * 2f / 360f;
        public const float Rad2Deg = 1f / Deg2Rad;

        // Mathf.Epsilon on x86 desktops, where flush-to-zero is off.
        public const float Epsilon = float.Epsilon;

        const float VectorEpsilon = 1e-5f;
        const float NormalSqrtEpsilon = 1e-15f;

        public static readonly Vector3 Up = new Vector3(0f, 1f, 0f);
        public static readonly Vector3 Down = new Vector3(0f, -1f, 0f);
        public static readonly Vector3 Right = new Vector3(1f, 0f, 0f);

        /// <summary>Stride's forward. Unity's +Z forward is mirrored to -Z.</summary>
        public static readonly Vector3 Forward = new Vector3(0f, 0f, -1f);

        // --- Mathf ---

        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Cos(float f) => (float)DetMath.Cos(f);
        public static float Sin(float f) => (float)DetMath.Sin(f);
        public static float Acos(float f) => (float)DetMath.Acos(f);
        public static float Atan2(float y, float x) => (float)DetMath.Atan2(y, x);
        public static float Abs(float f) => Math.Abs(f);

        // Not Math.Min/Max: those treat NaN differently.
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;

        public static float Sign(float f) => f >= 0f ? 1f : -1f;

        public static float Clamp(float value, float min, float max)
        {
            if (value < min)
                value = min;
            else if (value > max)
                value = max;
            return value;
        }

        public static float Clamp01(float value)
        {
            if (value < 0f)
                return 0f;
            if (value > 1f)
                return 1f;
            return value;
        }

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        public static float InverseLerp(float a, float b, float value)
        {
            if (a != b)
                return Clamp01((value - a) / (b - a));
            return 0f;
        }

        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Abs(target - current) <= maxDelta)
                return target;
            return current + Sign(target - current) * maxDelta;
        }

        public static float DeltaAngle(float current, float target)
        {
            float delta = Repeat(target - current, 360f);
            if (delta > 180f)
                delta -= 360f;
            return delta;
        }

        public static float Repeat(float t, float length)
            => Clamp(t - MathF.Floor(t / length) * length, 0f, length);

        // --- Vector3 ---

        public static Vector3 Add(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 Sub(Vector3 a, Vector3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 Mul(Vector3 a, float d) => new Vector3(a.X * d, a.Y * d, a.Z * d);
        public static Vector3 Div(Vector3 a, float d) => new Vector3(a.X / d, a.Y / d, a.Z / d);
        public static Vector3 Neg(Vector3 a) => new Vector3(-a.X, -a.Y, -a.Z);

        public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static float SqrMagnitude(Vector3 v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        public static float Magnitude(Vector3 v) => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

        /// <summary>Unity's Vector3.normalized: divides, and zero below 1e-5.</summary>
        public static Vector3 Normalized(Vector3 v)
        {
            float mag = Magnitude(v);
            if (mag > VectorEpsilon)
                return Div(v, mag);
            return Vector3.Zero;
        }

        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
        {
            float sqrMag = Dot(planeNormal, planeNormal);
            if (sqrMag < Epsilon)
                return vector;

            float dot = Dot(vector, planeNormal);
            return new Vector3(
                vector.X - planeNormal.X * dot / sqrMag,
                vector.Y - planeNormal.Y * dot / sqrMag,
                vector.Z - planeNormal.Z * dot / sqrMag);
        }

        public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
        {
            float toX = target.X - current.X;
            float toY = target.Y - current.Y;
            float toZ = target.Z - current.Z;
            float sqDist = toX * toX + toY * toY + toZ * toZ;

            if (sqDist == 0f || (maxDistanceDelta >= 0f && sqDist <= maxDistanceDelta * maxDistanceDelta))
                return target;

            float dist = (float)Math.Sqrt(sqDist);
            return new Vector3(
                current.X + toX / dist * maxDistanceDelta,
                current.Y + toY / dist * maxDistanceDelta,
                current.Z + toZ / dist * maxDistanceDelta);
        }

        public static float Angle(Vector3 from, Vector3 to)
        {
            float denominator = (float)Math.Sqrt(SqrMagnitude(from) * SqrMagnitude(to));
            if (denominator < NormalSqrtEpsilon)
                return 0f;

            float dot = Clamp(Dot(from, to) / denominator, -1f, 1f);
            return (float)DetMath.Acos(dot) * Rad2Deg;
        }

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Clamp01(t);
            return new Vector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);
        }

        // --- Vector2 ---

        public static float SqrMagnitude(Vector2 v) => v.X * v.X + v.Y * v.Y;
        public static float Magnitude(Vector2 v) => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);

        public static Vector2 ClampMagnitude(Vector2 vector, float maxLength)
        {
            float sqrMagnitude = SqrMagnitude(vector);
            if (sqrMagnitude > maxLength * maxLength)
            {
                float mag = (float)Math.Sqrt(sqrMagnitude);
                float x = vector.X / mag;
                float y = vector.Y / mag;
                return new Vector2(x * maxLength, y * maxLength);
            }

            return vector;
        }

        // --- Quaternion ---

        /// <summary>Unity's Quaternion.Euler(pitch, yaw, 0) for Stride angles: negated into Unity's
        /// convention, rotated there, mirrored back through z (exact in floating point). Float sin and
        /// cos like Unity's native code; bit-identical on 23,238 dumped angles (RotationProbe).</summary>
        public static Quaternion Euler(float pitch, float yaw)
        {
            float x = -pitch * Deg2Rad * 0.5f;
            float y = -yaw * Deg2Rad * 0.5f;
            float sx = DetMath.SinF(x), cx = DetMath.CosF(x);
            float sy = DetMath.SinF(y), cy = DetMath.CosF(y);

            // Unity order Z, X, Y with z = 0, then mirrored: (-x, -y, z, w).
            return new Quaternion(-(cy * sx), -(sy * cx), -(sy * sx), cy * cx);
        }

        /// <summary>Unity's Quaternion.Euler(0, yaw, 0) for a Stride yaw.</summary>
        public static Quaternion EulerYaw(float yaw)
        {
            float y = -yaw * Deg2Rad * 0.5f;
            return new Quaternion(0f, -DetMath.SinF(y), 0f, DetMath.CosF(y));
        }

        /// <summary>
        /// The Stride yaw of a rotation about the vertical axis, what
        /// Unity's transform.eulerAngles.y gave before the mirror.
        /// </summary>
        public static float YawOf(Quaternion q)
        {
            double unity = 2.0 * DetMath.Atan2(-q.Y, q.W) * (180.0 / Math.PI);
            unity %= 360.0;
            if (unity < 0.0)
                unity += 360.0;
            return -(float)unity;
        }

        /// <summary>Unity's Quaternion * Vector3, term for term, in float. Unity's editor
        /// Mono evaluated each sum in double, so the reference logs differ in the last
        /// bits; the IL2CPP builds rounded like this.</summary>
        public static Vector3 Rotate(Quaternion rotation, Vector3 point)
        {
            float num = rotation.X * 2f;
            float num2 = rotation.Y * 2f;
            float num3 = rotation.Z * 2f;
            float num4 = rotation.X * num;
            float num5 = rotation.Y * num2;
            float num6 = rotation.Z * num3;
            float num7 = rotation.X * num2;
            float num8 = rotation.X * num3;
            float num9 = rotation.Y * num3;
            float num10 = rotation.W * num;
            float num11 = rotation.W * num2;
            float num12 = rotation.W * num3;

            return new Vector3(
                (1f - (num5 + num6)) * point.X + (num7 - num12) * point.Y + (num8 + num11) * point.Z,
                (num7 + num12) * point.X + (1f - (num4 + num6)) * point.Y + (num9 - num10) * point.Z,
                (num8 - num11) * point.X + (num9 + num10) * point.Y + (1f - (num4 + num5)) * point.Z);
        }
    }
}
