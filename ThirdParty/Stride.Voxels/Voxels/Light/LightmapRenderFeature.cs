using Stride.Graphics;
using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Stride.Rendering.Voxels.VoxelGI
{
    /// <summary>KotH patch: meshes with a second UV set (TEXCOORD1) are drawn into the
    /// lightmap by <see cref="BakeStage"/>; once it is baked (<see cref="Ready"/>) they
    /// read it in the colour pass. Uses the post vertex stage hook.</summary>
    public sealed class LightmapRenderFeature : SubRenderFeature
    {
        static readonly ShaderSource Bake = new ShaderClassSource("LightmapBakePosition");
        static readonly ShaderSource Skip = new ShaderClassSource("LightmapSkip");
        static readonly ShaderSource Read = ReadSource();

        public RenderStage MainStage { get; set; }
        public RenderStage BakeStage { get; set; }

        /// <summary>The lightmap is baked: the meshes read it.</summary>
        public bool Ready { get; set; }

        /// <summary>Lightmapped meshes this frame, and those whose bake effect is compiled.</summary>
        public int Meshes { get; private set; }
        public int BakeEffectsCompiled { get; private set; }

        static ShaderSource ReadSource()
        {
            var mixin = new ShaderMixinSource();
            mixin.AddMacro("KOTH_LIGHTMAP", 1);
            return mixin;
        }

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            var root = (RootEffectRenderFeature)RootRenderFeature;
            var effects = root.RenderData.GetData(root.RenderEffectKey);
            int slots = root.EffectPermutationSlotCount;
            int main = Slot(root, MainStage), bake = Slot(root, BakeStage);
            int meshes = 0, compiled = 0;
            foreach (RenderObject renderObject in root.RenderObjects)
            {
                // The bake stage takes only meshes with a second UV set; the rest draw nothing there.
                bool lit = renderObject is RenderMesh mesh && HasSecondUV(mesh);
                if (lit)
                    meshes++;
                for (int i = 0; i < slots; i++)
                {
                    ShaderSource extension = i == bake ? (lit ? Bake : Skip) : lit && Ready && i == main ? Read : null;
                    RenderEffect effect = effects[renderObject.StaticObjectNode * slots + i];
                    if (extension == null || effect == null || !effect.IsUsedDuringThisFrame(RenderSystem))
                        continue;
                    effect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ExtensionPostVertexStageShader, extension);
                    if (lit && i == bake && effect.State == RenderEffectState.Normal && effect.Effect != null)
                        compiled++;
                }
            }
            Meshes = meshes;
            BakeEffectsCompiled = compiled;
        }

        static int Slot(RootEffectRenderFeature root, RenderStage stage)
            => stage == null ? -1 : root.GetEffectPermutationSlot(stage).Index;

        /// <summary>The mesh has a second UV set: it is part of the map, lightmapped and never moves.</summary>
        public static bool HasSecondUV(RenderMesh mesh)
        {
            foreach (var buffer in (mesh.ActiveMeshDraw ?? mesh.Mesh?.Draw)?.VertexBuffers ?? [])
                foreach (var element in buffer.Declaration.VertexElements)
                    if (element.SemanticName == "TEXCOORD" && element.SemanticIndex == 1)
                        return true;
            return false;
        }
    }

    /// <summary>KotH patch: the lightmap is drawn in UV space, from both sides, without depth.</summary>
    public sealed class LightmapBakePipelineProcessor : PipelineProcessor
    {
        public RenderStage BakeStage { get; set; }

        public override void Process(RenderNodeReference renderNodeReference, ref RenderNode renderNode, RenderObject renderObject, PipelineStateDescription pipelineState)
        {
            if (renderNode.RenderStage != BakeStage)
                return;
            pipelineState.RasterizerState = RasterizerStates.CullNone;
            pipelineState.DepthStencilState = DepthStencilStates.None;
            pipelineState.BlendState = BlendStates.Opaque;
        }
    }
}
