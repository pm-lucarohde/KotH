using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Marks the player body for interpolation; presentation only, nothing
    /// here is simulated. A rigidly parented mesh would show the 40 Hz tick
    /// in its shadow and in any third-person view. PlayerVisualSystem does
    /// the actual work for every body at once.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public sealed class PlayerVisual : MonoBehaviour
    {
        [SerializeField] PlayerMotor motor;

        public PlayerMotor Motor => motor;

        void OnEnable()
        {
            // Explicit == null: Unity's fake null makes ?? and is null lie.
            if (motor == null)
            {
                Debug.LogWarning("PlayerVisual without a motor stays still.", this);
                return;
            }

            PlayerVisualSystem.Register(this);
        }

        void OnDisable()
        {
            PlayerVisualSystem.Unregister(this);
        }
    }
}
