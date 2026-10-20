using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Threading;
using Stride.Engine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Interpolates every player body in one place: one update for the whole
    /// game instead of one per player, spread across worker threads. Each
    /// visual is its own root entity, so its transform is written alone and
    /// the 40 Hz snap of the simulated body never shows.
    /// </summary>
    [DataContract("KotH.PlayerVisualSystem")]
    public sealed class PlayerVisualSystem : SyncScript
    {
        readonly List<Entity> visuals = new List<Entity>();
        readonly List<PlayerMotor> motors = new List<PlayerMotor>();

        // The rotations of the last two ticks per body: the motor keeps only
        // the latest, and a figure turning in third person would show the
        // 40 Hz steps. Presentation only, nothing goes back to the motor.
        readonly List<Quaternion> fromRotations = new List<Quaternion>();
        readonly List<Quaternion> toRotations = new List<Quaternion>();
        int seenTick = -1;

        public PlayerVisualSystem()
        {
            // After the driver, before nothing in particular.
            Priority = 500;
        }

        /// <summary>
        /// Shows motor through visual, a model in true size for a standing
        /// body, origin at the body centre, facing -Z.
        /// </summary>
        public void Register(Entity visual, PlayerMotor motor)
        {
            visuals.Add(visual);
            motors.Add(motor);
            fromRotations.Add(motor.Rotation);
            toRotations.Add(motor.Rotation);
        }

        public void Unregister(Entity visual)
        {
            int i = visuals.IndexOf(visual);
            if (i < 0)
                return;
            visuals.RemoveAt(i);
            motors.RemoveAt(i);
            fromRotations.RemoveAt(i);
            toRotations.RemoveAt(i);
        }

        public override void Update()
        {
            int count = visuals.Count;
            if (count == 0)
                return;

            float alpha = TickInterpolation.Alpha;

            // Several ticks in one frame start from the last shown rotation,
            // as near as this gets to the tick before without asking the motor.
            int tick = Services.GetService<SimulationSystem>()?.CurrentTick ?? 0;
            if (tick != seenTick)
            {
                seenTick = tick;
                for (int i = 0; i < count; i++)
                {
                    fromRotations[i] = toRotations[i];
                    toRotations[i] = motors[i].Rotation;
                }
            }

            // Every body writes only its own transform, so the bodies split
            // freely; nothing is read back from a transform.
            Dispatcher.For(0, count, i =>
            {
                PlayerMotor motor = motors[i];
                TransformComponent t = visuals[i].Transform;
                t.Position = UMath.Lerp(motor.PreviousBodyCenter, motor.BodyCenter, alpha);
                t.Rotation = Quaternion.Slerp(fromRotations[i], toRotations[i], alpha);

                // Crouching squashes the model to the collider's height.
                t.Scale = new Vector3(1f, motor.Height / motor.StandHeight, 1f);
            });
        }
    }
}
