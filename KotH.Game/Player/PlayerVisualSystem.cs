using System;
using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Animations;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Threading;
using Stride.Engine;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>
    /// Interpolates every player body in one place: one update for the whole
    /// game instead of one per player, spread across worker threads. Each
    /// visual is its own root entity, so its transform is written alone and
    /// the 40 Hz snap of the simulated body never shows. Also plays the
    /// figure's clips: the idle loop while a body stands still, else the
    /// rest pose, and a blink every few seconds, set on the lid bones.
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

        /// <userdoc>Sekunden zwischen zwei Lidschlägen einer Figur.</userdoc>
        [Display("Blinzelabstand", "Animation")] public float BlinkInterval = 15f;

        /// <userdoc>Wie lange ein Lidschlag dauert, zu und wieder auf.</userdoc>
        [Display("Blinzeldauer", "Animation")] public float BlinkSeconds = 0.15f;

        /// <userdoc>Überblendzeit zwischen Ruhe und Grundhaltung.</userdoc>
        [Display("Überblenden", "Animation")] public float BlendSeconds = 0.25f;

        /// <summary>Clip names in every visual's AnimationComponent.</summary>
        public const string IdleClip = "Idle", RestClip = "Rest";

        // Lids open: shrunk to their pivot, as no triangle at all.
        const float Hidden = 1e-4f;
        static readonly string[] LidBones = { "Lid.L", "Lid.R" };

        readonly List<Figure> figures = new List<Figure>();
        readonly Random random = new Random();

        sealed class Figure
        {
            public AnimationComponent Animation;
            public ModelComponent Model;
            public bool Idle, Started;
            public float BlinkClock;
            public int[] Lids;
        }

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
            figures.Add(new Figure
            {
                Animation = visual.Get<AnimationComponent>(),
                Model = visual.Get<ModelComponent>(),
                // Figures side by side never blink together.
                BlinkClock = (float)random.NextDouble() * BlinkInterval,
            });
        }

        /// <summary>Every figure blinks now, for KOTH_SHOT_BLINK.</summary>
        public void BlinkAll()
        {
            foreach (Figure f in figures)
                f.BlinkClock = BlinkInterval;
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
            figures.RemoveAt(i);
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

            float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            for (int i = 0; i < count; i++)
                Animate(figures[i], motors[i], dt);
        }

        void Animate(Figure f, PlayerMotor motor, float dt)
        {
            if (f.Animation != null)
            {
                Vector3 v = motor.Velocity;
                bool idle = motor.IsGrounded && !motor.IsCrouching && !motor.IsSliding
                            && v.X * v.X + v.Z * v.Z < 0.01f && motor.KnockLeft <= 0f;
                if (!f.Started)
                {
                    // Each figure starts somewhere in the loop: no choir of breaths.
                    PlayingAnimation play = f.Animation.Play(idle ? IdleClip : RestClip);
                    if (idle)
                        play.CurrentTime = TimeSpan.FromSeconds(random.NextDouble() * play.Clip.Duration.TotalSeconds);
                    f.Started = true;
                    f.Idle = idle;
                }
                else if (idle != f.Idle)
                {
                    f.Animation.Crossfade(idle ? IdleClip : RestClip, TimeSpan.FromSeconds(BlendSeconds));
                    f.Idle = idle;
                }
            }
            Blink(f, dt);
        }

        void Blink(Figure f, float dt)
        {
            SkeletonUpdater skeleton = f.Model?.Skeleton;
            if (skeleton == null)
                return;
            if (f.Lids == null)
            {
                f.Lids = new int[LidBones.Length];
                for (int k = 0; k < LidBones.Length; k++)
                    f.Lids[k] = Array.FindIndex(skeleton.Nodes, n => n.Name == LidBones[k]);
            }

            // Down in the first third, held a moment, up in the last half.
            f.BlinkClock += dt;
            if (f.BlinkClock >= BlinkInterval + BlinkSeconds)
                f.BlinkClock -= BlinkInterval;
            float t = (f.BlinkClock - BlinkInterval) / BlinkSeconds;
            float closed = t <= 0f || t >= 1f ? 0f
                         : t < 0.33f ? t / 0.33f
                         : t < 0.5f ? 1f
                         : 1f - (t - 0.5f) / 0.5f;

            // The lid bones point down: their own Y is the lid's height.
            Vector3 scale = closed > 0f ? new Vector3(1f, closed, 1f) : new Vector3(Hidden);
            foreach (int lid in f.Lids)
                if (lid >= 0)
                    skeleton.NodeTransformations[lid].Transform.Scale = scale;
        }
    }
}
