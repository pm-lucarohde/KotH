using System.Linq;
using Stride.Engine;
using Stride.Rendering;
using Stride.Shaders;

namespace KotH.Client.Player
{
    /// <summary>
    /// Draws the first-person arms and stick in front of everything (Luca:
    /// they reached into walls): every mesh of the group gets FirstPersonDepth
    /// after its vertex stage, in the colour pass and the depth prepass alike.
    /// Added to the compositor's mesh feature at run time.
    /// </summary>
    public sealed class ArmsInFront : SubRenderFeature
    {
        static readonly ShaderClassSource Shader = new ShaderClassSource("FirstPersonDepth");

        /// <summary>The render group of the arms.</summary>
        public RenderGroup Group { get; set; }

        /// <summary>Hooks it into the scene's compositor once.</summary>
        public static void Install(SceneSystem scene, RenderGroup group)
        {
            var meshes = scene.GraphicsCompositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            if (meshes != null && !meshes.RenderFeatures.OfType<ArmsInFront>().Any())
                meshes.RenderFeatures.Add(new ArmsInFront { Group = group });
        }

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            var root = (RootEffectRenderFeature)RootRenderFeature;
            var effects = root.RenderData.GetData(root.RenderEffectKey);
            int slots = root.EffectPermutationSlotCount;
            foreach (RenderObject renderObject in root.RenderObjects)
            {
                if (renderObject.RenderGroup != Group)
                    continue;
                for (int i = 0; i < slots; i++)
                {
                    RenderEffect effect = effects[renderObject.StaticObjectNode * slots + i];
                    if (effect != null && effect.IsUsedDuringThisFrame(RenderSystem))
                        effect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ExtensionPostVertexStageShader, Shader);
                }
            }
        }
    }
}
