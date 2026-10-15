using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Client.Player
{
    /// <summary>
    /// The first-person view as pure functions of a motor, so the probes
    /// measure the very code the camera runs (they compile this file in).
    /// </summary>
    public static class ViewMath
    {
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
            // The near plane is a rectangle, not a point: its corners sit
            // near * sqrt(1 + t^2 + (aspect*t)^2) from the eye. Recomputed
            // per frame because the aspect follows the window.
            float t = (float)System.Math.Tan(verticalFovDegrees * 0.5f * UMath.Deg2Rad);
            float wide = aspect * t;
            float corner = UMath.Sqrt(1f + t * t + wide * wide);

            // Above the eye there is only standHeight - standEyeHeight of
            // body, 0.12 m, far less than the radius. That is what a ceiling
            // resting on the head has to clear.
            float crown = motor.BodyCenter.Y + motor.Height * 0.5f - motor.EyePosition.Y;
            float room = UMath.Min(crown, motor.Radius) * margin;

            return UMath.Clamp(room / corner, 0.01f, 0.3f);
        }
    }
}
