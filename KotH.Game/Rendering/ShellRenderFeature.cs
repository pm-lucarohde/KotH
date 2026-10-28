using System.Linq;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Voxels.VoxelGI;
using Stride.Shaders;

namespace KotH.Client.Rendering
{
    /// <summary>Once the lightmap is baked, mixes ShellLight and KOTH_LIGHTMAP into the
    /// colour pass of the shells' group: they read the light of the sand under them.
    /// Their lightmap uv is not TEXCOORD1, so LightmapRenderFeature leaves them out (the
    /// bake waits for every mesh with one). Added at run time.</summary>
    public sealed class ShellRenderFeature : SubRenderFeature
    {
        static readonly ShaderSource Lit = LitSource();

        /// <summary>The render group of the shells.</summary>
        public RenderGroup Group { get; set; }

        LightmapRenderFeature lightmap;

        static ShaderSource LitSource()
        {
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource("ShellLight"));
            mixin.AddMacro("KOTH_LIGHTMAP", 1);
            return mixin;
        }

        /// <summary>Hooks it into the scene's compositor once.</summary>
        public static void Install(SceneSystem scene, RenderGroup group)
        {
            var meshes = scene.GraphicsCompositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            if (meshes != null && !meshes.RenderFeatures.OfType<ShellRenderFeature>().Any())
                meshes.RenderFeatures.Add(new ShellRenderFeature { Group = group });
        }

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            var root = (RootEffectRenderFeature)RootRenderFeature;
            // The forward renderer adds it on its first frame, maybe after the shells.
            lightmap ??= (root as MeshRenderFeature)?.RenderFeatures.OfType<LightmapRenderFeature>().FirstOrDefault();
            if (lightmap is not { Ready: true, MainStage: { } stage })
                return;
            var effects = root.RenderData.GetData(root.RenderEffectKey);
            int slots = root.EffectPermutationSlotCount;
            int main = root.GetEffectPermutationSlot(stage).Index;
            foreach (RenderObject renderObject in root.RenderObjects)
            {
                if (renderObject.RenderGroup != Group)
                    continue;
                RenderEffect effect = effects[renderObject.StaticObjectNode * slots + main];
                if (effect != null && effect.IsUsedDuringThisFrame(RenderSystem))
                    effect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ExtensionPostVertexStageShader, Lit);
            }
        }
    }
}
