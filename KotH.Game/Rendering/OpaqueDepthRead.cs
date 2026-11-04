using System.Linq;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace KotH.Client.Rendering
{
    /// <summary>The colour pass tests the depth prepass' depth without writing it: the prepass holds every
    /// opaque surface already, so the same fragments pass and the same depth stays. With depth writes on, a
    /// shader that may discard (alpha-tested leaves) lost the early depth test and lit every hidden leaf. The
    /// leaves test Equal: under LessEqual the cut-out parts of every card in front passed and were lit first.</summary>
    public sealed class OpaqueDepthRead : PipelineProcessor
    {
        RenderStage opaque;

        /// <summary>Hooks it into the scene's compositor once, for the stage named Opaque.</summary>
        public static void Install(SceneSystem scene)
        {
            var compositor = scene.GraphicsCompositor;
            var meshes = compositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            RenderStage opaque = compositor?.RenderStages.FirstOrDefault(s => s.Name == "Opaque");
            if (meshes != null && opaque != null && !meshes.PipelineProcessors.OfType<OpaqueDepthRead>().Any())
                meshes.PipelineProcessors.Add(new OpaqueDepthRead { opaque = opaque });
        }

        public override void Process(RenderNodeReference renderNodeReference, ref RenderNode renderNode, RenderObject renderObject,
                                     PipelineStateDescription pipelineState)
        {
            if (renderNode.RenderStage != opaque)
                return;
            pipelineState.DepthStencilState.DepthBufferWriteEnable = false;
            if (renderObject is RenderMesh mesh && LeafRenderFeature.IsLeaves(mesh))
                pipelineState.DepthStencilState.DepthBufferFunction = CompareFunction.Equal;
        }
    }
}
