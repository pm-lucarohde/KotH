using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
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
    /// with the distance to the blocker (PCSS). The map casts once into the static
    /// map of <see cref="StaticSunShadowRenderer"/>, which the receivers multiply in
    /// (lightmapped meshes baked per texel), so the cascades hold only the figures.
    /// </summary>
    [DataContract("CascadeShadowMapRenderer")]
    public sealed class CascadeShadowMapRenderer : LightDirectionalShadowMapRenderer
    {
        static readonly ValueParameterKey<Vector4> CascadeRects = ParameterKeys.NewValue<Vector4>(default, "CascadeShadowReceiver.CascadeRects");
        static readonly ValueParameterKey<float> SunTangent = ParameterKeys.NewValue<float>(default, "CascadeShadowReceiver.SunTangent");
        static readonly ValueParameterKey<float> MaxPenumbraKey = ParameterKeys.NewValue<float>(default, "CascadeShadowReceiver.MaxPenumbra");
        static readonly ValueParameterKey<Matrix> WorldToStaticUV = ParameterKeys.NewValue<Matrix>(default, "CascadeShadowReceiver.WorldToStaticUV");
        static readonly ValueParameterKey<Vector2> StaticTexelSize = ParameterKeys.NewValue<Vector2>(default, "CascadeShadowReceiver.StaticTexelSize");
        static readonly ObjectParameterKey<Texture> StaticShadowMap = ParameterKeys.NewObject<Texture>(null, "CascadeShadowReceiver.StaticShadowMap");
        static readonly ObjectParameterKey<Texture> SunLightmap = ParameterKeys.NewObject<Texture>(null, "CascadeShadowReceiver.SunLightmap");

        /// <summary>Angular radius of the sun in degrees: the penumbra per metre between blocker and receiver.</summary>
        /// <userdoc>Scheinbarer Radius der Sonne in Grad: Je größer, desto breiter der Halbschatten pro Meter Abstand zum Werfer.</userdoc>
        [DataMember(100)]
        public float SunAngle { get; set; } = 1.0f;

        /// <summary>Widest penumbra radius in shadow map texels; the blockers are searched as far.</summary>
        /// <userdoc>Größter Radius des Halbschattens in Texeln der Schattenkarte; so weit wird auch nach Werfern gesucht.</userdoc>
        [DataMember(110)]
        public float MaxPenumbra { get; set; } = 4.0f;

        /// <summary>Share of Stride's size for each cascade's map (1 = as Stride picks it).</summary>
        /// <userdoc>Anteil der von Stride gewählten Größe jeder Kaskade (1 = unverändert); kleiner ist günstiger und weicher.</userdoc>
        [DataMember(120)]
        public float Resolution { get; set; } = 1.0f;

        public override LightShadowMapTexture CreateShadowMapTexture(RenderView renderView, RenderLight renderLight, IDirectLight light, int shadowMapSize)
            => base.CreateShadowMapTexture(renderView, renderLight, light, Math.Max(16, (int)MathF.Round(shadowMapSize * Resolution)));

        public override ILightShadowMapShaderGroupData CreateShaderGroupData(LightShadowType shadowType)
            => new Receiver(this, (LightShadowMapShaderGroupDataBase)base.CreateShaderGroupData(shadowType),
                1 << ((int)(shadowType & LightShadowType.CascadeMask) - 1));

        /// <summary>Stride's group data, with the receiver shader swapped and its parameters added.</summary>
        sealed class Receiver(CascadeShadowMapRenderer owner, LightShadowMapShaderGroupDataBase inner, int cascadeCount) : ILightShadowMapShaderGroupData
        {
            Vector4[] rects = [];
            ValueParameterKey<Vector4> rectsKey;
            ValueParameterKey<float> tangentKey;
            ValueParameterKey<float> penumbraKey;
            ValueParameterKey<Matrix> staticUVKey;
            ValueParameterKey<Vector2> staticTexelKey;
            ObjectParameterKey<Texture> staticMapKey;
            ObjectParameterKey<Texture> sunLightmapKey;

            public void ApplyShader(ShaderMixinSource mixin) => inner.ApplyShader(mixin);

            public void UpdateLayout(string compositionName)
            {
                inner.UpdateLayout(compositionName);
                rectsKey = CascadeRects.ComposeWith(compositionName);
                tangentKey = SunTangent.ComposeWith(compositionName);
                penumbraKey = MaxPenumbraKey.ComposeWith(compositionName);
                staticUVKey = WorldToStaticUV.ComposeWith(compositionName);
                staticTexelKey = StaticTexelSize.ComposeWith(compositionName);
                staticMapKey = StaticShadowMap.ComposeWith(compositionName);
                sunLightmapKey = SunLightmap.ComposeWith(compositionName);
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
                parameters.Set(penumbraKey, owner.MaxPenumbra);

                // The static map once it is complete; until then (or never) the cascade atlas stands in, unread.
                var still = context.RenderContext.Tags.Get(StaticSunShadowRenderer.Key);
                bool ready = still is { Ready: true, Texture: not null };
                Texture stand = currentLights.Count > 0 ? ((ShaderData)currentLights[0].ShadowMapTexture.ShaderData).Texture : null;
                parameters.Set(staticMapKey, ready ? still.Texture : stand);
                parameters.Set(sunLightmapKey, context.RenderContext.Tags.Get(Stride.Rendering.Voxels.ForwardRendererVoxels.SunLightmapKey) ?? stand);
                parameters.Set(staticUVKey, ready ? still.WorldToUV : Matrix.Identity);
                parameters.Set(staticTexelKey, ready ? new Vector2(1f / still.Texture.Width) : Vector2.Zero);
            }

            public void ApplyDrawParameters(RenderDrawContext context, ParameterCollection parameters,
                Stride.Core.Collections.FastListStruct<Stride.Rendering.Lights.LightDynamicEntry> currentLights,
                ref BoundingBoxExt boundingBox)
                => inner.ApplyDrawParameters(context, parameters, currentLights, ref boundingBox);
        }
    }
}
