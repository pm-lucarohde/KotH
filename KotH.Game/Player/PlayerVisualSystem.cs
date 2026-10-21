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
using Stride.Particles.Components;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>
    /// Interpolates every player body in one place: one update for the whole
    /// game instead of one per player, spread across worker threads. Each
    /// visual is its own root entity, so its transform is written alone and
    /// the 40 Hz snap of the simulated body never shows. Also plays the
    /// figure's clips, all at once with weights: the idle loop while a body
    /// stands still, walking and running in four directions blended by
    /// speed and heading, the rest pose otherwise, and a blink every few
    /// seconds, set on the lid bones. The leaves of skirt and collar swing
    /// on springs in the wind of the figure's own motion, and the mouth
    /// opens and shuts a little, calm as breathing.
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
        [Display("Blinzelabstand", "Animation")] public float BlinkInterval = 10f;

        /// <userdoc>Wie lange ein Lidschlag dauert, zu und wieder auf.</userdoc>
        [Display("Blinzeldauer", "Animation")] public float BlinkSeconds = 0.15f;

        /// <userdoc>Überblendzeit zwischen Ruhe, Gehen, Rennen, Ducken, Gleiten und Grundhaltung.</userdoc>
        [Display("Überblenden", "Animation")] public float BlendSeconds = 0.25f;

        /// <userdoc>Schritte pro Sekunde bei Gehtempo; die Füße rutschen etwas, wenn das kleiner ist als das Tempo verlangt.</userdoc>
        [Display("Schritte Gehen", "Animation")] public float WalkSteps = 3.5f;

        /// <userdoc>Schritte pro Sekunde bei Sprinttempo.</userdoc>
        [Display("Schritte Rennen", "Animation")] public float RunSteps = 6f;

        /// <userdoc>Schritte pro Sekunde beim geduckten Gehen.</userdoc>
        [Display("Schritte geduckt", "Animation")] public float CrouchSteps = 3f;

        /// <userdoc>Breite der Figur geduckt und beim Gleiten (1 = wie stehend). Schmaler wirkt die gestauchte Figur weniger platt.</userdoc>
        [Display("Breite geduckt", "Animation")] public float CrouchWidth = 0.9f;

        /// <userdoc>So viele Meter ragt die Kappe geduckt und beim Gleiten über die Hülle hinaus; weniger Stauchung, bessere Proportionen. Dort zählt kein Treffer.</userdoc>
        [Display("Kappe über der Hülle", "Animation")] public float CrouchOverhang = 0.1f;

        /// <userdoc>Wie weit sich der Mund im Atemrhythmus öffnet und schließt (0,1 = um 10 % seiner Höhe).</userdoc>
        [Display("Mund", "Animation")] public float MouthBreath = 0.08f;

        /// <userdoc>Wie lange die Figur nach einer Landung einfedert, in Sekunden; jede Landung gleich stark.</userdoc>
        [Display("Einfedern", "Animation")] public float LandSeconds = 0.3f;

        /// <userdoc>Wie tief sie dabei einfedert (1 = ganz in die Landepose).</userdoc>
        [Display("Aufprall", "Animation")] public float LandStrength = 1f;

        /// <userdoc>Wie lange der Tritt in die Luft beim Doppelsprung dauert, in Sekunden.</userdoc>
        [Display("Doppelsprung-Tritt", "Animation")] public float KickSeconds = 0.35f;

        /// <userdoc>Wie weit die Blätter des Rocks bei Sprinttempo nach hinten wehen, in Grad; langsamer entsprechend weniger, im Stand gar nicht.</userdoc>
        [Display("Wehen", "Blätter")] public float LeafSwing = 60f;

        /// <userdoc>Anteil, mit dem der Kragen mitweht (1 = so stark wie der Rock).</userdoc>
        [Display("Kragen", "Blätter")] public float CollarShare = 0.45f;

        /// <userdoc>Flattern der Blätter bei Sprinttempo, in Grad.</userdoc>
        [Display("Flattern", "Blätter")] public float LeafFlutter = 10f;

        /// <userdoc>Wie weit sich der Rock beim Fallen nach außen hebt, in Grad.</userdoc>
        [Display("Fallwind", "Blätter")] public float LeafLift = 40f;

        /// <userdoc>Wie sehr Drehen die Blätter mitnimmt: so viele Meter vom Körper weg wirkt die Drehung wie Fahrtwind.</userdoc>
        [Display("Drehwind", "Blätter")] public float TurnRadius = 1f;

        /// <userdoc>Partikeleffekt, der beim Doppelsprung an den Füßen der Figur startet.</userdoc>
        [Display("Sporen beim Doppelsprung", "Effekte")] public Prefab SporeBurst;

        /// <userdoc>Wie weit der Kopf dem Blick nach oben und unten folgt (1 = ganz).</userdoc>
        [Display("Kopf folgt Blick", "Animation")] public float HeadFollow = 0.5f;

        /// <userdoc>Stärkste Neigung des Kopfes in Grad.</userdoc>
        [Display("Kopfneigung höchstens", "Animation")] public float HeadMaxDegrees = 40f;

        /// <summary>
        /// Clip names in every visual's AnimationComponent: idle, rest,
        /// walking and running forward, back, left and right, then crouched
        /// still, crouched walking, the slide, falling, the landing's give,
        /// rising after a jump and the kick of the double jump.
        /// </summary>
        public static readonly string[] Clips =
            { "Idle", "Rest", "WalkF", "WalkB", "WalkL", "WalkR", "RunF", "RunB", "RunL", "RunR",
              "Crouch", "CrouchWalk", "Slide", "Fall", "Land", "Jump", "SkyWalk" };
        const int IdleIndex = 0, RestIndex = 1, WalkIndex = 2, RunIndex = 6;
        const int CrouchIndex = 10, CrouchWalkIndex = 11, SlideIndex = 12, FallIndex = 13, LandIndex = 14;
        const int JumpIndex = 15, KickIndex = 16;

        // A landing only counts after this long in the air: stepping down a
        // stair or snapping to the ground does not give at the knees.
        const float LandMinAir = 0.15f;
        const float LandAttack = 0.06f, KickAttack = 0.05f;
        // A jump in the air shows as a sudden climb; gravity alone only slows it.
        const float KickBoost = 3f;

        // How much lower than standing each clip's pose is, in metres (printed
        // by .work/rig/rig.py). The rest of the way down to the collider's
        // height the figure is squashed.
        static readonly float[] PoseDrops = { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.207f, 0.189f, 0.209f, 0f, 0f, 0f, 0f };

        // Lids open: shrunk to their pivot, as no triangle at all.
        const float Hidden = 1e-4f;
        static readonly string[] LidBones = { "Lid.L", "Lid.R" };
        const string LookBone = "Look";
        const string MouthBone = "Mouth";

        // Leaf bones from .work/rig/rig.py, evenly round the body from the
        // front to the right: six on the skirt, four on the collar.
        const int SkirtLeaves = 6, CollarLeaves = 4;
        // A spring a little under critical: stopping swings the leaves past.
        const float LeafSpring = 150f, LeafDamping = 14f;

        readonly List<Figure> figures = new List<Figure>();
        readonly Random random = new Random();

        sealed class Figure
        {
            public AnimationComponent Animation;
            public ModelComponent Model;
            public PlayingAnimation[] Plays;
            public readonly float[] Weights = new float[Clips.Length];
            public SimulationBody Body;
            public float Phase, BlinkClock;
            public float AirTime, LandAge = float.MaxValue, KickAge = float.MaxValue, LastClimb;
            public int[] Lids;
            public int Look = -2, Mouth = -2;
            public int[] Leaves;
            public Vector2[] LeafBend, LeafRate;
            public float LeafClock, Yaw, Spin, MouthClock;
            public bool HasYaw, Kicked;
            public Entity Spores;
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
                Body = motor.Entity.Get<SimulationBody>(),
                // Figures side by side never blink together.
                BlinkClock = (float)random.NextDouble() * BlinkInterval,
                MouthClock = (float)random.NextDouble() * 30f,
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
            if (figures[i].Spores is { } spores)
                spores.Scene?.Entities.Remove(spores);
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
            });

            float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            for (int i = 0; i < count; i++)
                Animate(figures[i], motors[i], visuals[i].Transform, dt, alpha);
        }

        void Animate(Figure f, PlayerMotor motor, TransformComponent t, float dt, float alpha)
        {
            float drop = f.Animation != null ? Blend(f, motor, dt) : 0f;
            // A crouched pose is lower than standing; whatever is left to the
            // collider's height is squashed, feet kept on the collider's floor.
            // Narrower as well and the cap a little above the collider, so it
            // looks less flat (Luca's choice).
            float crouched = Crouched(motor);
            float scale = (motor.Height + CrouchOverhang * crouched) / (motor.StandHeight - drop);
            float width = 1f - (1f - CrouchWidth) * crouched;
            if (f.Kicked)
                Spores(f, t.Position - new Vector3(0f, 0.5f * motor.Height, 0f));
            t.Scale = new Vector3(width, scale, width);
            t.Position.Y += 0.5f * (motor.StandHeight * scale - motor.Height);
            Blink(f, dt);
            Breathe(f, dt);
            LookUpDown(f, alpha);
            Leaves(f, motor, t, dt);
        }

        /// <summary>
        /// The double jump puffs spores from the feet: one effect entity per
        /// figure, made on the first kick and restarted at the feet on every
        /// one after. Its particles live in world space and stay behind.
        /// </summary>
        void Spores(Figure f, Vector3 feet)
        {
            f.Kicked = false;
            if (SporeBurst == null)
                return;
            if (f.Spores == null)
            {
                f.Spores = SporeBurst.Instantiate()[0];
                f.Spores.Transform.Position = feet;
                Entity.Scene.Entities.Add(f.Spores);
                return;
            }
            f.Spores.Transform.Position = feet;
            f.Spores.Get<ParticleSystemComponent>()?.ParticleSystem.ResetSimulation();
        }

        /// <summary>
        /// Every leaf bone bends its tips with the wind of the motion: back
        /// leaves trail most, a fall lifts the skirt outwards, faster means
        /// a little flutter. Turning moves every leaf sideways as well: its
        /// own wind. Bends are tip offsets in radians, on springs.
        /// </summary>
        void Leaves(Figure f, PlayerMotor motor, TransformComponent t, float dt)
        {
            SkeletonUpdater skeleton = f.Model?.Skeleton;
            if (skeleton == null)
                return;
            if (f.Leaves == null)
            {
                f.Leaves = new int[SkirtLeaves + CollarLeaves];
                for (int k = 0; k < f.Leaves.Length; k++)
                {
                    string name = k < SkirtLeaves ? $"Leaf.S{k}" : $"Leaf.C{k - SkirtLeaves}";
                    f.Leaves[k] = Array.FindIndex(skeleton.Nodes, n => n.Name == name);
                }
                f.LeafBend = new Vector2[f.Leaves.Length];
                f.LeafRate = new Vector2[f.Leaves.Length];
                f.LeafClock = (float)random.NextDouble() * 10f;
            }

            // Air against the figure, in its own frame: X right, Z back.
            Vector3 local = Vector3.Transform(motor.Velocity, Quaternion.Invert(motor.Rotation));
            var flat = new Vector2(local.X, local.Z);
            float lift = motor.IsGrounded ? 0f : Math.Clamp(-local.Y / 12f, 0f, 1f);
            dt = MathF.Min(dt, 1f / 30f);
            f.LeafClock += dt;

            // Turn rate of the shown figure about +Y, a little smoothed.
            Vector3 facing = Vector3.Transform(-Vector3.UnitZ, t.Rotation);
            float yaw = MathF.Atan2(-facing.X, -facing.Z);
            float turn = 0f;
            if (f.HasYaw && dt > 0f)
            {
                float step = yaw - f.Yaw;
                step -= MathUtil.TwoPi * MathF.Round(step / MathUtil.TwoPi);
                turn = Math.Clamp(step / dt, -15f, 15f);
            }
            f.Yaw = yaw;
            f.HasYaw = true;
            f.Spin += (turn - f.Spin) * (1f - MathF.Exp(-dt / 0.05f));

            for (int k = 0; k < f.Leaves.Length; k++)
            {
                bool skirt = k < SkirtLeaves;
                int count = skirt ? SkirtLeaves : CollarLeaves;
                int i = skirt ? k : k - SkirtLeaves;
                float a = MathUtil.TwoPi * i / count;
                var outward = new Vector2(MathF.Sin(a), -MathF.Cos(a));
                float share = skirt ? 1f : CollarShare;
                // The leaf's own motion: the body's plus the turn's at its place.
                Vector2 moving = flat + f.Spin * TurnRadius * new Vector2(outward.Y, -outward.X);
                float speed = moving.Length();
                float pace = Math.Clamp(speed / motor.SprintSpeed, 0f, 1f);
                Vector2 heading = speed > 1e-3f ? moving / speed : Vector2.Zero;
                // Downwind leaves stream freely, upwind ones lie against the body.
                float trail = 0.5f + 0.5f * MathF.Max(0f, -Vector2.Dot(outward, heading));
                // Each leaf flutters at its own rate, so they never move as one.
                float w = f.LeafClock * (9f + 1.7f * k);
                float flutter = MathUtil.DegreesToRadians(LeafFlutter) * pace;
                Vector2 target = (-heading * MathUtil.DegreesToRadians(LeafSwing) * pace * trail
                                + outward * (MathUtil.DegreesToRadians(LeafLift) * lift + flutter * MathF.Sin(w))
                                + new Vector2(-outward.Y, outward.X) * flutter * 0.5f * MathF.Sin(1.3f * w + k)) * share;

                f.LeafRate[k] += (LeafSpring * (target - f.LeafBend[k]) - LeafDamping * f.LeafRate[k]) * dt;
                f.LeafBend[k] += f.LeafRate[k] * dt;
                int node = f.Leaves[k];
                if (node < 0)
                    continue;
                // A hanging tip moves along m when turned about down x m.
                Vector2 m = f.LeafBend[k];
                float angle = m.Length();
                Quaternion bend = angle > 1e-5f
                    ? Quaternion.RotationAxis(new Vector3(-m.Y / angle, 0f, m.X / angle), angle)
                    : Quaternion.Identity;
                skeleton.NodeTransformations[node].Transform.Rotation = bend * skeleton.Nodes[node].Transform.Rotation;
            }
        }

        /// <summary>
        /// The mouth bone stretches the painted mouth: two slow waves that
        /// never line up, so it opens every two to four seconds, never quite
        /// alike. A little narrower while open. No clip touches it.
        /// </summary>
        void Breathe(Figure f, float dt)
        {
            SkeletonUpdater skeleton = f.Model?.Skeleton;
            if (skeleton == null)
                return;
            if (f.Mouth == -2)
                f.Mouth = Array.FindIndex(skeleton.Nodes, n => n.Name == MouthBone);
            if (f.Mouth < 0)
                return;
            f.MouthClock += dt;
            float t = f.MouthClock;
            float wave = 0.6f * MathF.Sin(MathUtil.TwoPi * t / 3.1f) + 0.4f * MathF.Sin(MathUtil.TwoPi * t / 4.7f + 1.3f);
            // The mouth bone points down: its own Y is the mouth's height.
            skeleton.NodeTransformations[f.Mouth].Transform.Scale =
                new Vector3(1f - 0.35f * MouthBreath * wave, 1f + MouthBreath * wave, 1f);
        }

        /// <summary>The neck bone tilts with the view; no clip touches it.</summary>
        void LookUpDown(Figure f, float alpha)
        {
            SkeletonUpdater skeleton = f.Model?.Skeleton;
            if (skeleton == null || f.Body == null)
                return;
            if (f.Look == -2)
                f.Look = Array.FindIndex(skeleton.Nodes, n => n.Name == LookBone);
            if (f.Look < 0)
                return;
            float pitch = f.Body.PreviousLookPitch + (f.Body.LookPitch - f.Body.PreviousLookPitch) * alpha;
            float tilt = Math.Clamp(pitch * HeadFollow, -HeadMaxDegrees, HeadMaxDegrees);
            // The neck points up, its X is the figure's right: positive tips the head back.
            skeleton.NodeTransformations[f.Look].Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(tilt));
        }

        /// <summary>Sets the clip weights; returns how much lower than standing the mixed pose is.</summary>
        float Blend(Figure f, PlayerMotor motor, float dt)
        {
            if (f.Plays == null)
            {
                // Every clip plays from the start on; only the weights change.
                f.Plays = new PlayingAnimation[Clips.Length];
                for (int i = 0; i < Clips.Length; i++)
                    if (f.Animation.Animations.ContainsKey(Clips[i]))
                        f.Plays[i] = f.Animation.Add(f.Animation.Animations[Clips[i]], 0, AnimationBlendOperation.LinearBlend, 1f, 0f, AnimationRepeatMode.LoopInfinite);
                // Each figure breathes at its own point of the loop.
                foreach (int i in new[] { IdleIndex, CrouchIndex })
                    if (f.Plays[i] is { } breathing)
                        breathing.CurrentTime = TimeSpan.FromSeconds(random.NextDouble() * breathing.Clip.Duration.TotalSeconds);
            }

            Vector3 v = motor.Velocity;
            float speed = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
            bool footing = motor.IsGrounded && !motor.IsSliding && motor.KnockLeft <= 0f;
            float moving = footing ? Smooth(0.15f, 1.2f, speed) : 0f;
            bool still = footing && speed < 0.1f;
            float run = Math.Clamp((speed - motor.WalkSpeed) / (motor.SprintSpeed - motor.WalkSpeed), 0f, 1f);
            float crouched = Crouched(motor);
            float stand = 1f - crouched;
            float slide = motor.IsSliding ? 1f : 0f;
            // Rising shows the jump, falling the fall; at the top the rest pose.
            bool airborne = !motor.IsGrounded;
            float falling = airborne ? Smooth(0f, 3f, -v.Y) : 0f;
            float rising = airborne ? Smooth(0f, 3f, v.Y) : 0f;
            if (airborne && f.AirTime > 0.05f && motor.KnockLeft <= 0f && v.Y - f.LastClimb > KickBoost)
            {
                f.KickAge = 0f;
                f.Kicked = true;
            }
            f.LastClimb = v.Y;
            f.KickAge += dt;
            if (airborne)
                f.AirTime += dt;
            else
            {
                if (f.AirTime > LandMinAir)
                    f.LandAge = 0f;
                f.AirTime = 0f;
            }
            f.LandAge += dt;

            // Heading in the figure's own frame (it faces -Z): forward, back, left, right.
            Vector3 local = Vector3.Transform(v, Quaternion.Invert(motor.Rotation));
            float forward = -local.Z, right = local.X;
            float sum = MathF.Max(1e-4f, MathF.Abs(forward) + MathF.Abs(right));
            Span<float> heading = stackalloc float[4];
            heading[0] = MathF.Max(0f, forward) / sum;
            heading[1] = MathF.Max(0f, -forward) / sum;
            heading[2] = MathF.Max(0f, -right) / sum;
            heading[3] = MathF.Max(0f, right) / sum;

            Span<float> target = stackalloc float[Clips.Length];
            target[IdleIndex] = stand * (1f - moving) * (still ? 1f : 0f);
            target[RestIndex] = stand * (1f - moving) * (still ? 0f : 1f) * (1f - falling - rising);
            target[FallIndex] = stand * (1f - moving) * (still ? 0f : 1f) * falling;
            target[JumpIndex] = stand * (1f - moving) * (still ? 0f : 1f) * rising;
            for (int d = 0; d < 4; d++)
            {
                target[WalkIndex + d] = stand * moving * (1f - run) * heading[d];
                target[RunIndex + d] = stand * moving * run * heading[d];
            }
            target[CrouchIndex] = crouched * (1f - slide) * (1f - moving);
            target[CrouchWalkIndex] = crouched * (1f - slide) * moving;
            target[SlideIndex] = crouched * slide;

            // One phase for all gaits and headings: their feet touch down together.
            float stepsPerSecond = WalkSteps + (RunSteps - WalkSteps) * run;
            float cycleMetres = 2f * (motor.WalkSpeed + (motor.SprintSpeed - motor.WalkSpeed) * run) / stepsPerSecond;
            cycleMetres += (2f * motor.CrouchSpeed / CrouchSteps - cycleMetres) * crouched;
            f.Phase = (f.Phase + speed / cycleMetres * dt) % 1f;

            float follow = 1f - MathF.Exp(-3f * dt / MathF.Max(1e-3f, BlendSeconds));
            // The landing is a short pulse over everything else, not followed:
            // quickly in, eased out. Crouched or sliding it gives nothing.
            float land = f.LandAge < LandAttack ? f.LandAge / LandAttack
                       : 1f - Smooth(LandAttack, MathF.Max(LandAttack + 1e-3f, LandSeconds), f.LandAge);
            land *= LandStrength * stand * (1f - slide);
            float kick = f.KickAge < KickAttack ? f.KickAge / KickAttack
                       : 1f - Smooth(KickAttack, MathF.Max(KickAttack + 1e-3f, KickSeconds), f.KickAge);
            kick *= stand;
            float total = 0f, drop = 0f;
            for (int i = 0; i < Clips.Length; i++)
            {
                f.Weights[i] += (target[i] - f.Weights[i]) * follow;
                if (f.Plays[i] is not { } play)
                    continue;
                float weight = i == LandIndex ? land : i == KickIndex ? kick
                             : f.Weights[i] * MathF.Max(0f, 1f - land - kick);
                play.Weight = weight < 1e-3f ? 0f : weight;
                // Stride mixes by weight over the sum of weights, so does the drop.
                total += play.Weight;
                drop += play.Weight * PoseDrops[i];
                if (i >= WalkIndex && i < RunIndex + 4 || i == CrouchWalkIndex)
                {
                    play.TimeFactor = 0f;
                    play.CurrentTime = TimeSpan.FromTicks((long)(f.Phase * play.Clip.Duration.Ticks));
                }
            }
            return total > 0f ? drop / total : 0f;
        }

        /// <summary>0 standing, 1 crouched; follows the collider's height, which glides between both.</summary>
        static float Crouched(PlayerMotor motor) =>
            Math.Clamp((motor.StandHeight - motor.Height) / MathF.Max(1e-3f, motor.StandHeight - motor.CrouchHeight), 0f, 1f);

        static float Smooth(float e0, float e1, float x)
        {
            float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
            return t * t * (3f - 2f * t);
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
