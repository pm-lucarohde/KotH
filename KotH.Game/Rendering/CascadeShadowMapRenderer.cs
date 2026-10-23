using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Rendering;
using Stride.Rendering.Shadows;
using Stride.Shaders;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Stride's directional shadow map renderer, receiving with
    /// <c>CascadeShadowReceiver</c>: depth bias in metres plus texels of each
    /// cascade and PCF taps along the receiver's plane, instead of one share of
    /// every cascade's range (stripes near, shadows cut off from the feet farther
    /// away) and lines at the ground's edges inside soft shadows. Penumbrae widen
    /// with the distance to the blocker (PCSS).
    /// </summary>
    [DataContract("CascadeShadowMapRenderer")]
    public sealed class CascadeShadowMapRenderer : LightDirectionalShadowMapRenderer
    {
        static readonly ValueParameterKey<Vector4> CascadeRects = ParameterKeys.NewValue<Vector4>(default, "CascadeShadowReceiver.CascadeRects");
        static readonly ValueParameterKey<float> SunTangent = ParameterKeys.NewValue<float>(default, "CascadeShadowReceiver.SunTangent");

        /// <summary>Angular radius of the sun in degrees: the penumbra per metre between blocker and receiver.</summary>
        /// <userdoc>Scheinbarer Radius der Sonne in Grad: Je größer, desto breiter der Halbschatten pro Meter Abstand zum Werfer.</userdoc>
        [DataMember(100)]
        public float SunAngle { get; set; } = 1.0f;

        public override ILightShadowMapShaderGroupData CreateShaderGroupData(LightShadowType shadowType)
            => new Receiver(this, (LightShadowMapShaderGroupDataBase)base.CreateShaderGroupData(shadowType),
                1 << ((int)(shadowType & LightShadowType.CascadeMask) - 1));

        /// <summary>Stride's group data, with the receiver shader swapped and its parameters added.</summary>
        sealed class Receiver(CascadeShadowMapRenderer owner, LightShadowMapShaderGroupDataBase inner, int cascadeCount) : ILightShadowMapShaderGroupData
        {
            Vector4[] rects = [];
            ValueParameterKey<Vector4> rectsKey;
            ValueParameterKey<float> tangentKey;

            public void ApplyShader(ShaderMixinSource mixin) => inner.ApplyShader(mixin);

            public void UpdateLayout(string compositionName)
            {
                inner.UpdateLayout(compositionName);
                rectsKey = CascadeRects.ComposeWith(compositionName);
                tangentKey = SunTangent.ComposeWith(compositionName);
            }

            public void UpdateLightCount(int lightLastCount, int lightCurrentCount)
            {
                inner.UpdateLightCount(lightLastCount, lightCurrentCount);
                var mixins = inner.ShadowShader.Mixins;
                var stride = mixins[0];
                mixins[0] = new ShaderClassSource("CascadeShadowReceiver") { GenericArguments = stride.GenericArguments };
                Array.Resize(ref rects, cascadeCount * Math.Max(lightCurrentCount, 1));
            }

            public void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters,
                Stride.Core.Collections.FastListStruct<Stride.Rendering.Lights.LightDynamicEntry> currentLights)
            {
                inner.ApplyViewParameters(context, parameters, currentLights);
                for (int l = 0; l < currentLights.Count && (l + 1) * cascadeCount <= rects.Length; l++)
                {
                    var data = (ShaderData)currentLights[l].ShadowMapTexture.ShaderData;
                    Array.Copy(data.TextureCoords, 0, rects, l * cascadeCount, cascadeCount);
                }
                parameters.Set(rectsKey, rects.Length, ref rects[0]);
                parameters.Set(tangentKey, MathF.Tan(MathUtil.DegreesToRadians(owner.SunAngle)));
            }

            public void ApplyDrawParameters(RenderDrawContext context, ParameterCollection parameters,
                Stride.Core.Collections.FastListStruct<Stride.Rendering.Lights.LightDynamicEntry> currentLights,
                ref BoundingBoxExt boundingBox)
                => inner.ApplyDrawParameters(context, parameters, currentLights, ref boundingBox);
        }
    }
}
