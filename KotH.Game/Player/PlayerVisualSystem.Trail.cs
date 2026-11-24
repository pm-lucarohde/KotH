using KotH.Shared.Net;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles.Components;

namespace KotH.Client.Player
{
    public sealed partial class PlayerVisualSystem
    {
        /// <userdoc>Sparkle trail behind figures whose look has one (from level 100): one emitter per shape, named sparks, stars and spores.</userdoc>
        [Display("Sparkle trail", "Effects")] public Prefab GlowTrail;

        // The emitter for each FigureLook trail shape (none for NoTrail).
        static readonly string[] TrailEmitters = { null, "sparks", "stars", "spores" };

        /// <summary>The figure's sparkle trail follows its middle; the particles stay in the world behind
        /// it. In its model's render group, so the own trail hides in first person as the figure does.</summary>
        void Trail(Figure f, Vector3 middle)
        {
            if (f.Colors.TrailShape == FigureLook.NoTrail || GlowTrail == null)
                return;
            if (f.Trail == null)
            {
                Entity trail = GlowTrail.Instantiate()[0];
                trail.Transform.Position = middle;
                f.TrailParticles = trail.Get<ParticleSystemComponent>();
                if (f.TrailParticles != null)
                {
                    f.TrailParticles.Color = FigureColors.Trail(f.Colors);
                    foreach (var emitter in f.TrailParticles.ParticleSystem.Emitters)
                        emitter.CanEmitParticles = emitter.EmitterName == TrailEmitters[f.Colors.TrailShape];
                }
                Entity.Scene.Entities.Add(trail);
                f.Trail = trail;
            }
            f.Trail.Transform.Position = middle;
            if (f.TrailParticles != null && f.Model != null)
                f.TrailParticles.RenderGroup = f.Model.RenderGroup;
        }
    }
}
