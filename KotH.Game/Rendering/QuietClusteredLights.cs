using Stride.Core;
using Stride.Rendering;
using Stride.Rendering.Lights;

namespace KotH.Client.Rendering
{
    /// <summary>Stride's clustered point and spot lights, but only in frames that have one: Stride built the
    /// cluster grid, uploaded it (59 KB per view, a new 4 MB upload buffer every 0.2 s) and read it in every
    /// pixel even with none, and a round has none (only the menu figure's light).</summary>
    [DataContract("QuietClusteredLights")]
    public sealed class QuietClusteredLights : LightClusteredPointSpotGroupRenderer
    {
        bool lit;

        public override void Reset()
        {
            base.Reset();
            lit = false;
        }

        public override void ProcessLights(ProcessLightsParameters parameters)
        {
            int before = parameters.LightIndices.Count;
            base.ProcessLights(parameters);
            lit |= parameters.LightIndices.Count < before;
        }

        public override void PrepareResources(RenderDrawContext drawContext)
        {
            if (lit)
                base.PrepareResources(drawContext);
        }

        public override void UpdateShaderPermutationEntry(ForwardLightingRenderFeature.LightShaderPermutationEntry shaderEntry)
        {
            if (lit)
                base.UpdateShaderPermutationEntry(shaderEntry);
        }
    }
}
