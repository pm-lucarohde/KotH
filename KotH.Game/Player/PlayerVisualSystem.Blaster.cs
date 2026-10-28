using System;
using KotH.Shared.Combat;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles.Components;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>The blaster's looks: cane and gun swap in a dip of the hand, the gun is held up
    /// on the IK (ArmSwing.Hold) and turned onto the view, a shot kicks it back, the spores on
    /// the gun show the shots left, and the spores in flight follow the simulation's slots.</summary>
    public sealed partial class PlayerVisualSystem
    {
        /// <summary>Names of the held entities GameRoot hangs on the hand.</summary>
        public const string CaneEntity = "Held", BlasterEntity = "Blaster";

        /// <userdoc>A spore in flight: the glowing ball with its trail.</userdoc>
        [Display("Spore shot", "Effects")] public Prefab SporeShot;

        /// <userdoc>Puff of spores at the muzzle on a shot and where a spore lands.</userdoc>
        [Display("Spore puff", "Effects")] public Prefab SporePuff;

        /// <userdoc>How long the hand dips to swap cane and blaster, in seconds (the simulation's draw is 0.2 s).</userdoc>
        [Display("Draw", "Blaster")] public float DrawSeconds = 0.2f;

        /// <userdoc>How long a shot kicks the blaster back, in seconds.</userdoc>
        [Display("Kick", "Blaster")] public float GunKickSeconds = 0.18f;

        /// <userdoc>How far a shot tips the barrel up, in degrees.</userdoc>
        [Display("Kick tilt", "Blaster")] public float KickDegrees = 12f;

        /// <userdoc>How much the held blaster follows the arms' swing walking and running (1 = as the cane).</userdoc>
        [Display("Bob", "Blaster")] public float GunBob = 0.20f;

        /// <userdoc>How far a shot pushes the hand back, in metres.</userdoc>
        [Display("Kick back", "Blaster")] public float KickBack = 0.05f;

        // Hand goals relative to the shoulder, chest frame (X right, Y up, Z back):
        // aimed and dipped while swapping; Eye is the view's origin for the arms.
        readonly struct HoldPath
        {
            public readonly Vector3 Aim, Lowered, Eye;

            public HoldPath(Vector3 aim, Vector3 lowered, Vector3 eye)
            {
                Aim = aim;
                Lowered = lowered;
                Eye = eye;
            }

            // In front of the chest, the forearm forward.
            public static readonly HoldPath Figure = new HoldPath(
                new Vector3(-0.02f, -0.10f, -0.40f), new Vector3(0.12f, -0.34f, -0.12f), Vector3.Zero);

            // Low on the right of the view; the shoulder sits 0.5 m under the eye, 0.24 m right.
            public static readonly HoldPath Arms = new HoldPath(
                new Vector3(0.0f, 0.12f, -0.40f), new Vector3(0.05f, -0.06f, -0.24f), new Vector3(-0.24f, 0.5f, 0f));
        }

        // Spores of the first-person barrel meet the crosshair this far ahead.
        const float Converge = 30f;
        const string BreechBone = "Breech", MuzzleBone = "Muzzle";
        static readonly string[] AmmoBones = { "Ammo1", "Ammo2", "Ammo3" };

        /// <summary>One hand's blaster: the figure's or the first-person arms'.</summary>
        sealed class Gun
        {
            public HeldLeaf Cane, Model;
            public Blaster Blaster;
            public float Draw, KickAge = float.MaxValue;
            public bool Watching;
            public int SeenKick, SeenShot, SeenImpact;
            // Barrel and top in the grip bone's frame, the muzzle in the gun model's.
            public Vector3 Barrel, Top, Muzzle;
            public int[] Ammo;
            public readonly float[] AmmoScale = { 1f, 1f, 1f };
            public Entity[] Shots;
            public readonly int[] ShotAge = new int[BlasterState.Slots];
            public Entity Flash, Puff;

            /// <summary>How far the gun is up, 0 (cane) to 1; the torso turns with it.</summary>
            public float Raise => Draw <= 0.5f ? 0f : Smooth((Draw - 0.5f) * 2f);
        }

        /// <summary>The hand's cane and blaster as GameRoot.AttachHeld hung them; null without a blaster.</summary>
        static Gun FindGun(Entity visual, PlayerMotor motor)
        {
            HeldLeaf model = FindHeld(visual, BlasterEntity);
            Blaster blaster = motor.Entity.Get<Blaster>();
            if (model == null || blaster == null)
                return null;
            model.Model.Enabled = false;
            return new Gun { Cane = FindHeld(visual, CaneEntity), Model = model, Blaster = blaster };
        }

        /// <summary>Before the arm's IK: follows the switch and the shots, and hands the pose to
        /// swing. aim is the view in the chest's frame.</summary>
        void HoldGun(Gun g, ArmSwing swing, in HoldPath path, Vector3 aim, float dt)
        {
            BlasterState state = g.Blaster.State;
            float target = state.Selected ? 1f : 0f;
            if (!g.Watching)
            {
                // What a figure holds when it appears is simply there.
                g.Draw = target;
                g.SeenKick = g.SeenShot = g.Blaster.LastShotTick;
                g.SeenImpact = g.Blaster.LastImpactTick;
                g.Watching = true;
            }
            float step = dt / MathF.Max(1e-3f, DrawSeconds);
            g.Draw = g.Draw < target ? MathF.Min(target, g.Draw + step) : MathF.Max(target, g.Draw - step);

            if (g.Blaster.LastShotTick > g.SeenKick)
            {
                g.SeenKick = g.Blaster.LastShotTick;
                g.KickAge = 0f;
            }
            else if (g.KickAge < float.MaxValue)
                g.KickAge += dt;
            float kick = g.KickAge < GunKickSeconds ? Square(1f - g.KickAge / GunKickSeconds) : 0f;

            float raise = g.Raise;
            Vector3 hand = Vector3.Lerp(path.Lowered, path.Aim, raise) + new Vector3(0f, 0.3f, 1f) * (KickBack * kick);
            if (path.Eye != Vector3.Zero)
                aim = Vector3.Normalize(path.Eye + aim * Converge - hand);
            Vector3 side = Vector3.Cross(aim, Vector3.UnitY);
            if (kick > 0f && side.LengthSquared() > 1e-6f)
                aim = Vector3.Transform(aim, Quaternion.RotationAxis(Vector3.Normalize(side), MathUtil.DegreesToRadians(KickDegrees * kick)));

            bool shown = g.Draw > 0.5f;
            if (shown && g.Barrel == Vector3.Zero)
                Axes(g);
            swing.Hold(hand, Smooth(g.Draw), shown && g.Barrel != Vector3.Zero ? aim : Vector3.Zero,
                       Vector3.UnitY, g.Barrel, g.Top, GunBob);
        }

        /// <summary>After the arm's IK: shows cane or gun, turns the gun onto the aim in the fist,
        /// and shrinks the spores of spent shots.</summary>
        void ShowGun(Gun g, ArmSwing swing, float dt)
        {
            bool shown = g.Draw > 0.5f;
            g.Model.Model.Enabled = shown;
            if (g.Cane != null)
                g.Cane.Model.Enabled = !shown;
            if (!shown)
                return;
            Tip(g.Model, swing.GunTip);

            SkeletonUpdater skeleton = g.Model.Model.Skeleton;
            if (skeleton == null)
                return;
            g.Ammo ??= Array.ConvertAll(AmmoBones, name => Array.FindIndex(skeleton.Nodes, n => n.Name == name));
            int shots = g.Blaster.State.Shots;
            for (int i = 0; i < g.Ammo.Length; i++)
            {
                if (g.Ammo[i] < 0)
                    continue;
                // The front spores go first; a new shot pops back in.
                float want = shots > g.Ammo.Length - 1 - i ? 1f : 0f;
                g.AmmoScale[i] += (want - g.AmmoScale[i]) * (1f - MathF.Exp(-dt / 0.06f));
                skeleton.NodeTransformations[g.Ammo[i]].Transform.Scale = new Vector3(MathF.Max(g.AmmoScale[i], Hidden));
            }
        }

        /// <summary>Barrel and top of the gun in the grip bone's frame, from its bones at rest and
        /// the place GameRoot gave it on the bone.</summary>
        static void Axes(Gun g)
        {
            Skeleton skeleton = g.Model.Model.Model?.Skeleton;
            if (skeleton == null)
                return;
            Vector3 At(string name)
            {
                int i = Array.FindIndex(skeleton.Nodes, n => n.Name == name);
                if (i < 0)
                    return Vector3.Zero;
                Matrix m = Matrix.Identity;
                for (; i >= 0; i = skeleton.Nodes[i].ParentIndex)
                {
                    TransformTRS node = skeleton.Nodes[i].Transform;
                    m *= Matrix.Scaling(node.Scale) * Matrix.RotationQuaternion(node.Rotation) * Matrix.Translation(node.Position);
                }
                return m.TranslationVector;
            }
            Vector3 breech = At(BreechBone), muzzle = At(MuzzleBone), top = At(AmmoBones[1]);
            if (breech == muzzle)
                return;
            TransformComponent t = g.Model.Model.Entity.Transform;
            Quaternion turn = g.Model.HasGrip ? g.Model.GripTurn : t.Rotation;
            Vector3 barrel = Vector3.Normalize(Vector3.Transform(muzzle - breech, turn));
            Vector3 up = Vector3.Transform(top - breech, turn);
            g.Top = Vector3.Normalize(up - barrel * Vector3.Dot(barrel, up));
            g.Barrel = barrel;
            g.Muzzle = muzzle;
        }

        /// <summary>Where the gun's muzzle is drawn, as of the frame before.</summary>
        static Vector3 MuzzleAt(Gun g)
            => Vector3.TransformCoordinate(g.Muzzle, g.Model.Model.Entity.Transform.WorldMatrix);

        /// <summary>The spores of one body in flight, between the last two ticks; a puff at the muzzle
        /// on every shot (from muzzle, the gun the eye sees) and where a spore lands.</summary>
        void Flights(Gun g, Gun muzzle, float alpha)
        {
            if (SporeShot == null)
                return;
            Vector3 from = muzzle != null && muzzle.Barrel != Vector3.Zero ? MuzzleAt(muzzle) : Vector3.Zero;
            if (g.Blaster.LastShotTick > g.SeenShot)
            {
                g.SeenShot = g.Blaster.LastShotTick;
                if (from != Vector3.Zero)
                    g.Flash = Puff(g.Flash, from, 0.5f);
            }
            if (g.Blaster.LastImpactTick > g.SeenImpact)
            {
                g.SeenImpact = g.Blaster.LastImpactTick;
                g.Puff = Puff(g.Puff, g.Blaster.LastImpactPoint, 1f);
            }

            BlasterState state = g.Blaster.State;
            g.Shots ??= new Entity[BlasterState.Slots];
            for (int i = 0; i < BlasterState.Slots; i++)
            {
                BlasterShot shot = Blaster.Slot(ref state, i);
                ref Entity e = ref g.Shots[i];
                bool fresh = shot.Age > 0 && (g.ShotAge[i] == 0 || shot.Age < g.ShotAge[i]);
                g.ShotAge[i] = shot.Age;
                if (shot.Age == 0)
                {
                    if (e != null && e.Get<ModelComponent>() is { Enabled: true } ball)
                    {
                        // The ball goes, its trail fades where it is.
                        ball.Enabled = false;
                        Emit(e, false);
                    }
                    continue;
                }
                Vector3 now = shot.Position;
                Vector3 before = shot.Age == 1 && from != Vector3.Zero
                    ? from : now - shot.Velocity * SimulationSystem.TickSeconds;
                Vector3 at = Vector3.Lerp(before, now, alpha);
                if (e == null)
                {
                    e = SporeShot.Instantiate()[0];
                    e.Transform.Position = at;
                    Entity.Scene.Entities.Add(e);
                }
                else if (fresh)
                {
                    e.Transform.Position = at;
                    e.Get<ParticleSystemComponent>()?.ParticleSystem.ResetSimulation();
                }
                e.Transform.Position = at;
                if (shot.Velocity.LengthSquared() > 1e-6f)
                    e.Transform.Rotation = Between(Vector3.UnitZ, Vector3.Normalize(shot.Velocity));
                // Its first stretch passes right before the eye: only the trail shows.
                if (e.Get<ModelComponent>() is { } model)
                    model.Enabled = shot.Age > 1;
                Emit(e, true);
            }
        }

        // One puff entity reused per purpose, restarted like the hit sparkles.
        Entity Puff(Entity puff, Vector3 at, float scale)
        {
            if (SporePuff == null)
                return puff;
            bool fresh = puff == null;
            puff ??= SporePuff.Instantiate()[0];
            puff.Transform.Position = at;
            puff.Transform.Scale = new Vector3(scale);
            if (fresh)
                Entity.Scene.Entities.Add(puff);
            else
                puff.Get<ParticleSystemComponent>()?.ParticleSystem.ResetSimulation();
            return puff;
        }

        static void Emit(Entity e, bool on)
        {
            if (e.Get<ParticleSystemComponent>()?.ParticleSystem is { } system)
                foreach (var emitter in system.Emitters)
                    emitter.CanEmitParticles = on;
        }

        /// <summary>Removes a figure's spores and puffs from the scene.</summary>
        static void DropGun(Gun g)
        {
            if (g == null)
                return;
            if (g.Shots != null)
                foreach (Entity e in g.Shots)
                    e?.Scene?.Entities.Remove(e);
            g.Flash?.Scene?.Entities.Remove(g.Flash);
            g.Puff?.Scene?.Entities.Remove(g.Puff);
        }

        static float Square(float x) => x * x;

        static Quaternion Between(Vector3 from, Vector3 to)
        {
            Vector3 axis = Vector3.Cross(from, to);
            float sin = axis.Length(), cos = Vector3.Dot(from, to);
            if (sin < 1e-6f)
                return cos > 0f ? Quaternion.Identity : Quaternion.RotationY(MathF.PI);
            return Quaternion.RotationAxis(axis / sin, MathF.Atan2(sin, cos));
        }
    }
}
