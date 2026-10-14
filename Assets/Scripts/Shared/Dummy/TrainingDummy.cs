using KotH.Shared.Player;
using KotH.Shared.Simulation;
using UnityEngine;

namespace KotH.Shared.Dummy
{
    /// <summary>
    /// A body that never steers itself. Ticked like a player so gravity, the
    /// ground probe and the render interpolation all behave the same, and so
    /// a collision pushes it around exactly as it would push a player. A
    /// motor that is never ticked hangs in the air at a body centre of zero.
    /// </summary>
    public sealed class TrainingDummy : SimulationBody
    {
        MoveInput input;

        protected override void Awake()
        {
            base.Awake();

            // Tick writes the rotation from input.Yaw on every step, so the
            // facing placed in the editor has to be read once and fed back.
            // Leaving Yaw at zero would undo it 40 times a second.
            input = new MoveInput { Yaw = transform.eulerAngles.y };
        }

        public override MoveInput ReadInput() => input;
    }
}
