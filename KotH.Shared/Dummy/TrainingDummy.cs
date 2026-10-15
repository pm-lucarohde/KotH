using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Dummy
{
    /// <summary>
    /// A body that never steers itself. Ticked like a player so gravity, the
    /// ground probe and the render interpolation all behave the same, and so
    /// a collision pushes it around exactly as it would push a player. A
    /// motor that is never ticked hangs in the air at a body centre of zero.
    /// </summary>
    [DataContract("KotH.TrainingDummy")]
    [Display("Trainingspuppe")]
    [ComponentCategory("KotH")]
    public sealed class TrainingDummy : SimulationBody
    {
        MoveInput input;

        protected internal override void Attach(WorldQueries world)
        {
            base.Attach(world);

            // Simulate writes the rotation from input.Yaw on every step, so
            // the facing placed in the editor has to be read once and fed
            // back. Leaving Yaw at zero would undo it 40 times a second.
            input = new MoveInput { Yaw = UMath.YawOf(Entity.Transform.Rotation) };
        }

        public override MoveInput ReadInput() => input;
    }
}
