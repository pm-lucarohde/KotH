using System;
using KotH.Client.Rendering;
using KotH.Shared.Combat;
using KotH.Shared.Physics;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Particles.Components;
using Stride.Particles.Materials;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace KotH.Client.Player
{
    /// <summary>The dash's looks: the figure squashed in the fall, fists thrown up and driven
    /// down to the impact (Luca: like Hulk), and where it lands a burst of hexagonal shards
    /// cut from the ground it struck (GroundSurface picks the texture).</summary>
    public sealed partial class PlayerVisualSystem
    {
        /// <userdoc>Longest wind-up of the dash, fists thrown up; a short fall takes less (at most 40 % of it).</userdoc>
        [Display("Dash wind-up (s)", "Animation")] public float SlamRiseSeconds = 0.09f;

        /// <userdoc>How long the blow's pose is held after the dash lands.</userdoc>
        [Display("Dash blow held (s)", "Animation")] public float SlamHoldSeconds = 0.12f;

        /// <userdoc>How long the blow's pose takes to let go afterwards.</userdoc>
        [Display("Dash blow let go (s)", "Animation")] public float SlamRecoverSeconds = 0.3f;

        /// <summary>A figure's dash poses as clip weights, from its fall (visual only, nothing read back).</summary>
        sealed class SlamPose
        {
            public float Raise, Smash;
            public float Age = float.MaxValue, Since = float.MaxValue;   // seconds into the fall, after it
            public float Rise, Fall, BlowDrop, EndRaise, EndSmash;
            public bool Landed;
        }

        /// <summary>Wind-up, eased out, over the fall's first moment; then the blow, eased in like a
        /// hammer, by the share of the way down so it strikes as the feet do. Landed, the blow is held
        /// and let go; a dash ended in the air (a knock) lets go at once.</summary>
        void Slam(SlamPose s, Dash dash, PlayerMotor motor, bool was, float dt)
        {
            bool now = motor.IsSlamming;
            if (now && !was)
            {
                float speed = dash != null && dash.SlamSpeed > 0f ? dash.SlamSpeed : 15f;
                (s.Age, s.Since, s.BlowDrop) = (0f, float.MaxValue, -1f);
                s.Fall = MathF.Min(Drop(motor) / speed, 2f);
                s.Rise = MathF.Min(SlamRiseSeconds, 0.4f * s.Fall);
            }
            else if (!now && was)
                (s.Since, s.Landed, s.EndRaise, s.EndSmash) = (0f, motor.IsGrounded || motor.IsTouchingGround, s.Raise, s.Smash);

            if (now)
            {
                s.Age += dt;
                float t = Math.Clamp(s.Age / MathF.Max(1e-3f, s.Rise), 0f, 1f), blow = 0f;
                if (s.Age > s.Rise)
                {
                    float left = Drop(motor);
                    if (s.BlowDrop < 0f)
                        s.BlowDrop = left;
                    // Without ground in reach the expected time stands in for the way.
                    float q = s.BlowDrop > 0.05f && s.BlowDrop < float.MaxValue ? 1f - left / s.BlowDrop
                            : (s.Age - s.Rise) / MathF.Max(1e-3f, s.Fall - s.Rise);
                    q = Math.Clamp(q, 0f, 1f);
                    blow = q * q;
                }
                (s.Raise, s.Smash) = ((1f - (1f - t) * (1f - t)) * (1f - blow), blow);
            }
            else if (s.Since < float.MaxValue)
            {
                s.Since += dt;
                float hold = s.Landed ? SlamHoldSeconds : 0f;
                float keep = 1f - Smooth(hold, hold + MathF.Max(1e-3f, SlamRecoverSeconds), s.Since);
                (s.Raise, s.Smash) = s.Landed ? (0f, keep) : (s.EndRaise * keep, s.EndSmash * keep);
                if (s.Since > hold + SlamRecoverSeconds)
                    (s.Since, s.Raise, s.Smash) = (float.MaxValue, 0f, 0f);
            }
            else
                (s.Raise, s.Smash) = (0f, 0f);
        }

        // The feet's height over the ground below, float.MaxValue without ground in 200 m.
        static float Drop(PlayerMotor motor)
        {
            float bottom = 0.5f * motor.Height;
            return motor.World.Raycast(motor.BodyCenter, -Vector3.UnitY, bottom + 200f, WorldQueries.WorldMask, out QueryHit hit)
                ? MathF.Max(0f, hit.Distance - bottom) : float.MaxValue;
        }

        /// <userdoc>Shards thrown up where a dash lands; its texture is swapped for the ground's.</userdoc>
        [Display("Ground shards", "Effects")] public Prefab GroundShards;

        /// <userdoc>Shard textures by ground: plain (Test map), grass, sand, snow, rock.</userdoc>
        [Display("Shards: plain ground", "Effects")] public Texture GroundChip;
        [Display("Shards: grass", "Effects")] public Texture GrassChip;
        [Display("Shards: sand", "Effects")] public Texture SandChip;
        [Display("Shards: snow", "Effects")] public Texture SnowChip;
        [Display("Shards: rock", "Effects")] public Texture RockChip;

        /// <summary>The loaded map's surface map, null for one without (GameRoot sets it).</summary>
        [DataMemberIgnore] public GroundSurface Surface { get; set; }

        /// <summary>A landing the figure has not shown yet sounds the deep fall's impact
        /// and throws the shards of its ground.</summary>
        void Slam(Figure f)
        {
            Dash dash = f.Dash;
            if (dash == null || dash.LastSlamTick == f.SeenSlam)
                return;
            f.SeenSlam = dash.LastSlamTick;
            f.Slammed = true;
            Sounded?.Invoke(FigureSound.Slam, f.Motor, dash.LastSlamPoint, 1f);
            if (GroundShards == null)
                return;

            GroundSurface.Kind kind = Surface?.At(dash.LastSlamPoint) ?? GroundSurface.Kind.Ground;
            f.Shards ??= new Entity[5];
            ref Entity shards = ref f.Shards[(int)kind];
            bool fresh = shards == null;
            if (fresh)
            {
                // Swapped before the first draw, so the material is built with it.
                shards = GroundShards.Instantiate()[0];
                if (ChipOf(kind) is { } chip && shards.Get<ParticleSystemComponent>() is { } made)
                    foreach (var emitter in made.ParticleSystem.Emitters)
                        if (emitter.Material is ParticleMaterialComputeColor m && FindTexture(m.ComputeColor) is { } t)
                            t.Texture = chip;
            }
            shards.Transform.Position = dash.LastSlamPoint;
            if (fresh)
                Entity.Scene.Entities.Add(shards);
            else
                shards.Get<ParticleSystemComponent>()?.ParticleSystem.ResetSimulation();
        }

        Texture ChipOf(GroundSurface.Kind kind) => kind switch
        {
            GroundSurface.Kind.Grass => GrassChip,
            GroundSurface.Kind.Sand => SandChip,
            GroundSurface.Kind.Snow => SnowChip,
            GroundSurface.Kind.Rock => RockChip,
            _ => GroundChip,
        };

        // The texture node of a particle material's colour tree.
        static ComputeTextureColor FindTexture(IComputeColor c) => c switch
        {
            ComputeTextureColor t => t,
            ComputeBinaryColor b => FindTexture(b.LeftChild) ?? FindTexture(b.RightChild),
            _ => null,
        };

        /// <summary>Removes a figure's shards from the scene.</summary>
        static void DropShards(Entity[] shards)
        {
            if (shards == null)
                return;
            foreach (Entity e in shards)
                e?.Scene?.Entities.Remove(e);
        }
    }
}
