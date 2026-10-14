using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Places the camera at the player's eye point. Not a child of the player:
    /// the motor moves in 40 steps per second and a child would show those
    /// steps as stutter. Interpolates between the last two tick positions.
    /// </summary>
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        [SerializeField] PlayerMotor motor;
        [SerializeField] PlayerInputReader input;

        // LateUpdate, so the camera runs after all movement of this frame.
        void LateUpdate()
        {
            if (motor == null || input == null)
                return;

            float alpha = TickInterpolation.Alpha;

            transform.SetPositionAndRotation(
                Vector3.Lerp(motor.PreviousEyePosition, motor.EyePosition, alpha),
                Quaternion.Euler(input.Pitch, input.Yaw, 0f));
        }
    }
}
