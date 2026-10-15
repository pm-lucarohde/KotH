using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
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

        public PlayerVisualSystem()
        {
            // After the driver, before nothing in particular.
            Priority = 500;
        }

        /// <summary>Shows motor through visual, a capsule mesh 2 m tall and 1 m wide.</summary>
        public void Register(Entity visual, PlayerMotor motor)
        {
            visuals.Add(visual);
            motors.Add(motor);
        }

        public void Unregister(Entity visual)
        {
            int i = visuals.IndexOf(visual);
            if (i < 0)
                return;
            visuals.RemoveAt(i);
            motors.RemoveAt(i);
        }

        public override void Update()
        {
            int count = visuals.Count;
            if (count == 0)
                return;

            float alpha = TickInterpolation.Alpha;

            // Every body writes only its own transform, so the bodies split
            // freely; nothing is read back from a transform.
            Dispatcher.For(0, count, i =>
            {
                PlayerMotor motor = motors[i];
                TransformComponent t = visuals[i].Transform;
                t.Position = UMath.Lerp(motor.PreviousBodyCenter, motor.BodyCenter, alpha);

                // Matches the mesh to the collider in both measures.
                float xz = motor.Radius * 2f;
                t.Scale = new Vector3(xz, motor.Height * 0.5f, xz);
            });
        }
    }
}
