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
        /// <userdoc>Rings on the water round a wading figure; its emitter's rate is set by the speed.</userdoc>
        [Display("Water ripples", "Effects")] public Prefab WaterRipples;

        /// <userdoc>Rings a second for a figure standing in the water.</userdoc>
        [Display("Ripples standing (1/s)", "Effects")] public float RipplesStanding = 1.5f;

        /// <userdoc>Rings a second added per metre a second of walking speed.</userdoc>
        [Display("Ripples per speed", "Effects")] public float RipplesPerSpeed = 1.2f;

        // Rings lie this far over the water, clear of its waves.
        const float RippleLift = 0.02f;

        /// <summary>Wading or not from the feet (centre less half the height); in the water the
        /// figure's ring emitter follows it on the surface, out of it the emitter stops and its
        /// rings fade where they were left.</summary>
        void Wade(Figure f, PlayerMotor motor, Vector3 center)
        {
            Vector3 feet = center - new Vector3(0f, 0.5f * motor.Height, 0f);
            float level = 0f;
            f.Wet = Surface != null && Surface.InWater(feet, out level) && feet.Y > level - 2f;
            if (!f.Wet)
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
            if (fresh)
                Entity.Scene.Entities.Add(f.Ripples);
            if (f.Ripples.Get<ParticleSystemComponent>()?.ParticleSystem.Emitters[0] is not { } emitter)
                return;
            emitter.CanEmitParticles = true;
            // A new rate rebuilds the spawner's carry, so only in steps of half a ring a second.
            Vector3 v = motor.Velocity;
            float rate = RipplesStanding + RipplesPerSpeed * MathF.Sqrt(v.X * v.X + v.Z * v.Z);
            if (emitter.Spawners[0] is SpawnerPerSecond spawner && MathF.Abs(spawner.SpawnCount - rate) > 0.5f)
                spawner.SpawnCount = rate;
        }

        /// <summary>Removes a figure's ring emitter from the scene.</summary>
        static void DropRipples(Entity ripples) => ripples?.Scene?.Entities.Remove(ripples);
    }
}
