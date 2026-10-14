using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Shared.Dummy
{
    /// <summary>
    /// Drives a motor that never moves. Simulation, not presentation: the
    /// dummy is ticked like a player so gravity, the ground probe and the
    /// render interpolation all behave the same. A motor that is never
    /// ticked hangs in the air and reports a body centre of zero.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class TrainingDummy : MonoBehaviour
    {
        PlayerMotor motor;
        MoveInput input;

        void Awake()
        {
            motor = GetComponent<PlayerMotor>();

            // Tick writes the rotation from input.Yaw on every step, so the
            // facing placed in the editor has to be read once and fed back.
            // Leaving Yaw at zero would undo it 40 times a second.
            input = new MoveInput { Yaw = transform.eulerAngles.y };
        }

        void FixedUpdate()
        {
            motor.Tick(input, Time.fixedDeltaTime);
        }
    }
}
