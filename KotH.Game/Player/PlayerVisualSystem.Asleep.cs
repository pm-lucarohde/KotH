using KotH.Client.Rendering;
using KotH.Shared.Combat;
using Stride.Engine;
using Stride.Particles.Components;

namespace KotH.Client.Player
{
    public sealed partial class PlayerVisualSystem
    {
        /// <summary>A figure's one-shot effects, made when it comes in and asleep (emitters off) until
        /// their first use: made then, they were loaded, instanced and readied for drawing mid-round.</summary>
        void MakeAsleep(Figure f)
        {
            if (SporeBurst != null)
            {
                f.Spores = Asleep(SporeBurst.Instantiate()[0]);
                FigureColors.TintSpores(f.Spores, f.Colors);
            }
            if (HitSparkles != null)
                f.Sparkles = Asleep(HitSparkles.Instantiate()[0]);
            if (GroundShards != null)
            {
                f.Shards = new Entity[6];
                for (int k = 0; k < f.Shards.Length; k++)
                    if (Surface?.Has((GroundSurface.Kind)k) ?? k == (int)GroundSurface.Kind.Ground)
                        f.Shards[k] = Asleep(MakeShards((GroundSurface.Kind)k));
            }
            if (WaterRipples != null && Surface?.Has(GroundSurface.Kind.Water) == true)
                f.Ripples = Asleep(WaterRipples.Instantiate()[0]);
            if (f.Gun is { } g)
            {
                if (SporeShot != null)
                {
                    g.Shots = new Entity[BlasterState.Slots];
                    for (int i = 0; i < g.Shots.Length; i++)
                        g.Shots[i] = Asleep(Shot(g.Look));
                }
                if (SporePuff != null)
                {
                    g.Flash = Asleep(PuffOf(g.Look));
                    g.Puff = Asleep(PuffOf(g.Look));
                }
            }
        }

        Entity Asleep(Entity e)
        {
            if (e == null)
                return null;
            Emit(e, false);
            if (e.Get<ModelComponent>() is { } model)
                model.Enabled = false;
            Entity.Scene.Entities.Add(e);
            return e;
        }

        /// <summary>Starts a made effect's burst again where it stands, also its first, asleep till now.</summary>
        static void Wake(Entity e)
        {
            Emit(e, true);
            e.Get<ParticleSystemComponent>()?.ParticleSystem.ResetSimulation();
        }
    }
}
