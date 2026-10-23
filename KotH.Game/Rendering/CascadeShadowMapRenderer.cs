using Stride.Core;
using Stride.Rendering.Shadows;
using Stride.Shaders;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Stride's directional shadow map renderer, receiving with
    /// <c>CascadeShadowReceiver</c>: depth bias in metres plus texels of each
    /// cascade and PCF taps along the receiver's plane, instead of one share of
    /// every cascade's range (stripes near, shadows cut off from the feet farther
    /// away) and lines at the ground's edges inside soft shadows.
    /// </summary>
    [DataContract("CascadeShadowMapRenderer")]
    public sealed class CascadeShadowMapRenderer : LightDirectionalShadowMapRenderer
    {
        public override ILightShadowMapShaderGroupData CreateShaderGroupData(LightShadowType shadowType)
            => new Receiver((LightShadowMapShaderGroupDataBase)base.CreateShaderGroupData(shadowType));

        /// <summary>Stride's group data, with the receiver shader swapped.</summary>
        sealed class Receiver(LightShadowMapShaderGroupDataBase inner) : ILightShadowMapShaderGroupData
        {
            public void ApplyShader(ShaderMixinSource mixin) => inner.ApplyShader(mixin);

            public void UpdateLayout(string compositionName) => inner.UpdateLayout(compositionName);

            public void UpdateLightCount(int lightLastCount, int lightCurrentCount)
            {
                inner.UpdateLightCount(lightLastCount, lightCurrentCount);
                var mixins = inner.ShadowShader.Mixins;
                var stride = mixins[0];
                mixins[0] = new ShaderClassSource("CascadeShadowReceiver") { GenericArguments = stride.GenericArguments };
            }

            public void ApplyViewParameters(Stride.Rendering.RenderDrawContext context, Stride.Rendering.ParameterCollection parameters,
                Stride.Core.Collections.FastListStruct<Stride.Rendering.Lights.LightDynamicEntry> currentLights)
                => inner.ApplyViewParameters(context, parameters, currentLights);

            public void ApplyDrawParameters(Stride.Rendering.RenderDrawContext context, Stride.Rendering.ParameterCollection parameters,
                Stride.Core.Collections.FastListStruct<Stride.Rendering.Lights.LightDynamicEntry> currentLights,
                ref Stride.Core.Mathematics.BoundingBoxExt boundingBox)
                => inner.ApplyDrawParameters(context, parameters, currentLights, ref boundingBox);
        }
    }
}
