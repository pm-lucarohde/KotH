using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Client.Player
{
    /// <summary>
    /// Where the third-person camera may go: up from the eye to the pivot,
    /// then back from the pivot along the view. Both legs are swept by a
    /// sphere, so everything on them is free space.
    /// </summary>
    public readonly struct ThirdPersonPath
    {
        public readonly Vector3 Pivot;

        /// <summary>Unit vector from the pivot towards the camera.</summary>
        public readonly Vector3 Back;

        /// <summary>How far back from the pivot the sphere stays clear.</summary>
        public readonly float Free;

        public ThirdPersonPath(Vector3 pivot, Vector3 back, float free)
        {
            Pivot = pivot;
            Back = back;
            Free = free;
        }

        public Vector3 At(float distance) => UMath.Add(Pivot, UMath.Mul(Back, UMath.Min(distance, Free)));
    }

    /// <summary>
    /// The views as pure functions of a motor and the world, so the probes
    /// measure the very code the camera runs (they compile this file in).
    /// </summary>
    public static class ViewMath
    {
        // Kept off every face the sphere stopped at: Bepu reports a merely
        // touched face as overlapping, see WorldQueries.
        const float Skin = 0.005f;

        // One sweep sees each collider once; the arena has no more than this
        // many boxes near any one camera.
        static readonly QueryHit[] Hits = new QueryHit[64];

        /// <summary>The eye between the last two ticks, alpha from TickInterpolation.</summary>
        public static Vector3 Eye(PlayerMotor motor, float alpha) =>
            UMath.Lerp(motor.PreviousEyePosition, motor.EyePosition, alpha);

        /// <summary>
        /// Keeps the near plane inside the collision capsule. Only the body
        /// is collided, so a near plane reaching past it clips away whatever
        /// the player leans against and makes it see-through.
        /// </summary>
        public static float NearPlane(PlayerMotor motor, float verticalFovDegrees, float aspect, float margin)
        {
            float corner = Corner(verticalFovDegrees, aspect);

            // Above the eye there is only standHeight - standEyeHeight of
            // body, 0.12 m, far less than the radius. That is what a ceiling
            // resting on the head has to clear.
            float crown = motor.BodyCenter.Y + motor.Height * 0.5f - motor.EyePosition.Y;
            float room = UMath.Min(crown, motor.Radius) * margin;

            return UMath.Clamp(room / corner, 0.01f, 0.3f);
        }

        /// <summary>
        /// The free path of a third-person camera looking along pitch and yaw
        /// from eye, lifted by lift and at most distance back. The world
        /// mask only: other bodies never push the camera.
        /// </summary>
        public static ThirdPersonPath ThirdPerson(WorldQueries world, Vector3 eye, float yaw, float pitch,
                                                  float lift, float distance, float radius)
        {
            float up = Sweep(world, eye, UMath.Up, lift, radius);
            Vector3 pivot = UMath.Add(eye, UMath.Mul(UMath.Up, up));

            Vector3 back = UMath.Neg(UMath.Rotate(UMath.Euler(pitch, yaw), UMath.Forward));
            return new ThirdPersonPath(pivot, back, Sweep(world, pivot, back, distance, radius));
        }

        /// <summary>
        /// Near plane for a camera in a free sphere of radius: its corners
        /// stay inside, so no face is ever cut open.
        /// </summary>
        public static float NearPlane(float radius, float verticalFovDegrees, float aspect, float margin)
            => UMath.Clamp(radius * margin / Corner(verticalFovDegrees, aspect), 0.01f, 0.3f);

        /// <summary>
        /// The near plane is a rectangle, not a point: its corners sit this
        /// many times the near distance from the eye. Recomputed per frame
        /// because the aspect follows the window.
        /// </summary>
        public static float Corner(float verticalFovDegrees, float aspect)
        {
            float t = (float)System.Math.Tan(verticalFovDegrees * 0.5f * UMath.Deg2Rad);
            float wide = aspect * t;
            return UMath.Sqrt(1f + t * t + wide * wide);
        }

        /// <summary>How far a sphere moves along direction before it touches the world.</summary>
        static float Sweep(WorldQueries world, Vector3 origin, Vector3 direction, float length, float radius)
        {
            int count = world.SphereCastAll(origin, radius, direction, length, WorldQueries.WorldMask, Hits);
            float free = length;
            for (int i = 0; i < count; i++)
                free = UMath.Min(free, Hits[i].Distance - Skin);
            return UMath.Max(0f, free);
        }
    }
}
