using System.Linq;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Voxels.VoxelGI;
using Stride.Shaders;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Mixes GrassBlade into every pass of the grass group's meshes (after the
    /// vertex stage, the hook ArmsInFront uses); in the colour pass also
    /// GrassNudge and, once the lightmap is baked, KOTH_LIGHTMAP: the blades
    /// read the light of the ground under them (GrassBlade sets the uv).
    /// Added at run time.
    /// </summary>
    public sealed class GrassRenderFeature : SubRenderFeature
    {
        static readonly ShaderClassSource Blade = new ShaderClassSource("GrassBlade");
        static readonly ShaderSource Shaded = ColourSource(false), Lit = ColourSource(true);

        /// <summary>The render group of the grass.</summary>
        public RenderGroup Group { get; set; }

        LightmapRenderFeature lightmap;

        static ShaderSource ColourSource(bool lightmapped)
        {
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource("GrassBlade"));
            mixin.Mixins.Add(new ShaderClassSource("GrassNudge"));
            if (lightmapped)
                mixin.AddMacro("KOTH_LIGHTMAP", 1);
            return mixin;
        }

        /// <summary>Hooks it into the scene's compositor once.</summary>
        public static void Install(SceneSystem scene, RenderGroup group)
        {
            var meshes = scene.GraphicsCompositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            if (meshes != null && !meshes.RenderFeatures.OfType<GrassRenderFeature>().Any())
                meshes.RenderFeatures.Add(new GrassRenderFeature { Group = group });
        }

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            var root = (RootEffectRenderFeature)RootRenderFeature;
            // The forward renderer adds it on its first frame, maybe after the grass.
            lightmap ??= (root as MeshRenderFeature)?.RenderFeatures.OfType<LightmapRenderFeature>().FirstOrDefault();
            var effects = root.RenderData.GetData(root.RenderEffectKey);
            int slots = root.EffectPermutationSlotCount;
            int main = lightmap?.MainStage is { } stage ? root.GetEffectPermutationSlot(stage).Index : -1;
            ShaderSource colour = lightmap is { Ready: true } ? Lit : Shaded;
            foreach (RenderObject renderObject in root.RenderObjects)
            {
                if (renderObject.RenderGroup != Group)
                    continue;
                for (int i = 0; i < slots; i++)
                {
                    RenderEffect effect = effects[renderObject.StaticObjectNode * slots + i];
                    if (effect != null && effect.IsUsedDuringThisFrame(RenderSystem))
                        effect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ExtensionPostVertexStageShader, i == main ? colour : Blade);
                }
            }
        }
    }
}
