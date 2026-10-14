using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Places the camera at the player's eye point. Not a child of the player:
    /// the motor moves in 40 steps per second and a child would show those
    /// steps as stutter. Interpolates between the last two tick positions.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        [SerializeField] PlayerMotor motor;
        [SerializeField] PlayerInputReader input;

        // How much of the room around the eye the near plane may use.
        [SerializeField] float nearClipMargin = 0.7f;

        Camera cam;

        void Awake()
        {
            cam = GetComponent<Camera>();
        }

        // LateUpdate, so the camera runs after all movement of this frame.
        void LateUpdate()
        {
            if (motor == null || input == null)
                return;

            float alpha = TickInterpolation.Alpha;

            transform.SetPositionAndRotation(
                Vector3.Lerp(motor.PreviousEyePosition, motor.EyePosition, alpha),
                Quaternion.Euler(input.Pitch, input.Yaw, 0f));

            ApplyNearPlane();
        }

        /// <summary>
        /// Keeps the near plane inside the collision capsule. Only the body
        /// is collided, so a near plane reaching past it clips away whatever
        /// the player leans against and makes it see-through.
        /// </summary>
        void ApplyNearPlane()
        {
            // The near plane is a rectangle, not a point: its corners sit
            // near * sqrt(1 + t^2 + (aspect*t)^2) from the eye. Recomputed
            // per frame because the aspect follows the window.
            float t = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float wide = cam.aspect * t;
            float corner = Mathf.Sqrt(1f + t * t + wide * wide);

            // Above the eye there is only standHeight - standEyeHeight of
            // body, 0.12 m, which is far less than the radius. That is what
            // a ceiling resting on the head has to clear.
            float crown = motor.BodyCenter.y + motor.Height * 0.5f
                        - motor.EyePosition.y;
            float room = Mathf.Min(crown, motor.Radius) * nearClipMargin;

            cam.nearClipPlane = Mathf.Clamp(room / corner, 0.01f, 0.3f);
        }
    }
}
