using System.Linq;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Voxels.VoxelGI;
using Stride.Shaders;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Mixes LeafSway into every pass of the crowns' leaves (after the vertex stage, the hook
    /// GrassRenderFeature uses): the meshes of the map's model without lightmap UVs, which
    /// are the leaves and the clump cards (MapUV gives the rest a second uv). Added at run time.
    /// </summary>
    public sealed class LeafRenderFeature : SubRenderFeature
    {
        static readonly ShaderClassSource Sway = new ShaderClassSource("LeafSway");

        /// <summary>Hooks it into the scene's compositor once.</summary>
        public static void Install(SceneSystem scene)
        {
            var meshes = scene.GraphicsCompositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            if (meshes != null && !meshes.RenderFeatures.OfType<LeafRenderFeature>().Any())
                meshes.RenderFeatures.Add(new LeafRenderFeature());
        }

        /// <summary>A mesh of the map's model (lightmapped) that has no lightmap UV itself.</summary>
        static bool IsLeaves(RenderMesh mesh) =>
            !LightmapRenderFeature.HasSecondUV(mesh) && SunShadowStageSelector.IsStatic(mesh);

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            var root = (RootEffectRenderFeature)RootRenderFeature;
            var effects = root.RenderData.GetData(root.RenderEffectKey);
            int slots = root.EffectPermutationSlotCount;
            foreach (RenderObject renderObject in root.RenderObjects)
            {
                if (renderObject is not RenderMesh mesh || !IsLeaves(mesh))
                    continue;
                for (int i = 0; i < slots; i++)
                {
                    RenderEffect effect = effects[renderObject.StaticObjectNode * slots + i];
                    if (effect != null && effect.IsUsedDuringThisFrame(RenderSystem))
                        effect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ExtensionPostVertexStageShader, Sway);
                }
            }
        }
    }
}
