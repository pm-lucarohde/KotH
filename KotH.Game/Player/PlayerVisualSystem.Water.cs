using System;
using KotH.Client.Rendering;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles;
using Stride.Particles.Components;
using Stride.Particles.Spawners;

namespace KotH.Client.Player
{
    /// <summary>Wading (Pyramid's pond, where the surface map has water): rings spread on the water
    /// round a figure's legs, more the faster it goes, and steps and landings splash. Visual
    /// only, nothing is read back into the simulation.</summary>
    public sealed partial class PlayerVisualSystem
    {
        /// <userdoc>Wakes on the water behind a wading figure; rate and heading follow its velocity.</userdoc>
        [Display("Water ripples", "Effects")] public Prefab WaterRipples;

        /// <userdoc>Wakes a second for a figure standing in the water (0: only while moving).</userdoc>
        [Display("Ripples standing (1/s)", "Effects")] public float RipplesStanding = 0f;

        /// <userdoc>Wakes a second added per metre a second of walking speed.</userdoc>
        [Display("Ripples per speed", "Effects")] public float RipplesPerSpeed = 1.25f;

        // Wakes lie this far over the water, clear of its waves; below MinMoveSpeed no wake spawns.
        const float RippleLift = 0.02f, MinMoveSpeed = 0.35f;

        /// <summary>Wading or not from the feet (centre less half the height); while moving in the water
        /// the wake emitter follows the figure facing its velocity, standing still or leaving stops it.</summary>
        void Wade(Figure f, PlayerMotor motor, Vector3 center)
        {
            Vector3 feet = center - new Vector3(0f, 0.5f * motor.Height, 0f);
            float level = 0f;
            f.Wet = Surface != null && Surface.InWater(feet, out level) && feet.Y > level - 2f;
            Vector3 v = motor.Velocity;
            float speed = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
            bool moving = speed > MinMoveSpeed;
            if (!f.Wet || !moving)
            {
                if (f.Ripples?.Get<ParticleSystemComponent>()?.ParticleSystem.Emitters[0] is { } idle)
                    idle.CanEmitParticles = false;
                return;
            }
            if (WaterRipples == null)
                return;
            bool fresh = f.Ripples == null;
            f.Ripples ??= WaterRipples.Instantiate()[0];
            f.Ripples.Transform.Position = new Vector3(feet.X, level + RippleLift, feet.Z);
            // Local +Z of ShapeBuilderQuad is UV v = 0 (the bow of Ripple.png); turn it along velocity.
            f.Ripples.Transform.Rotation = Quaternion.RotationY(MathF.Atan2(v.X, v.Z));
            if (fresh)
                Entity.Scene.Entities.Add(f.Ripples);
            if (f.Ripples.Get<ParticleSystemComponent>()?.ParticleSystem.Emitters[0] is not { } emitter)
                return;
            emitter.CanEmitParticles = true;
            // A new rate rebuilds the spawner's carry, so only in steps of half a wake a second.
            float rate = RipplesStanding + RipplesPerSpeed * speed;
            if (emitter.Spawners[0] is SpawnerPerSecond spawner && MathF.Abs(spawner.SpawnCount - rate) > 0.5f)
                spawner.SpawnCount = rate;
        }

        /// <summary>Removes a figure's ring emitter from the scene.</summary>
        static void DropRipples(Entity ripples) => ripples?.Scene?.Entities.Remove(ripples);
    }
}
