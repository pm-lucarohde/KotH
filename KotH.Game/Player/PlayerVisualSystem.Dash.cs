using KotH.Client.Rendering;
using KotH.Shared.Combat;
using Stride.Core;
using Stride.Engine;
using Stride.Graphics;
using Stride.Particles.Components;
using Stride.Particles.Materials;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace KotH.Client.Player
{
    /// <summary>The dash's looks: the figure squashed in the fall, and where it lands a burst
    /// of hexagonal shards cut from the ground it struck (GroundSurface picks the texture).</summary>
    public sealed partial class PlayerVisualSystem
    {
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
