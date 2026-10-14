using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Draws the player body. Presentation only - nothing here is simulated.
    /// The motor moves the root in 40 steps per second; a rigidly parented
    /// mesh would show those steps in its shadow and in any third-person
    /// view, even though the camera itself is already interpolated.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public sealed class PlayerVisual : MonoBehaviour
    {
        [SerializeField] PlayerMotor motor;

        void LateUpdate()
        {
            if (motor == null)
                return;

            float alpha = Mathf.Clamp01(
                (Time.time - Time.fixedTime) / Time.fixedDeltaTime);

            // World position on purpose: this overrides the parent's snapping.
            transform.position = Vector3.Lerp(
                motor.PreviousBodyCenter, motor.BodyCenter, alpha);

            // A primitive capsule is 2 m tall at scale 1, so half the
            // controller height matches it to the collider when crouching.
            transform.localScale = new Vector3(1f, motor.Height * 0.5f, 1f);
        }
    }
}
