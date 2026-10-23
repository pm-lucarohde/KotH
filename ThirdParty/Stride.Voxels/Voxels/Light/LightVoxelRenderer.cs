// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Skyboxes;
using Stride.Shaders;
using Stride.Rendering.Voxels;
using Stride.Rendering.Shadows;
using Stride.Rendering.Lights;
using Stride.Engine.Processors;

namespace Stride.Rendering.Voxels.VoxelGI
{
    /// <summary>
    /// Light renderer for <see cref="LightVoxel"/>.
    /// </summary>
    public class LightVoxelRenderer : LightGroupRendererBase
    {
        private readonly Dictionary<RenderLight, LightVoxelShaderGroup> lightShaderGroupsPerVoxel = new Dictionary<RenderLight, LightVoxelShaderGroup>();
        private PoolListStruct<LightVoxelShaderGroup> pool = new PoolListStruct<LightVoxelShaderGroup>(8, CreateLightVoxelShaderGroup);

        public override Type[] LightTypes { get; } = { typeof(LightVoxel) };

        public LightVoxelRenderer()
        {
            IsEnvironmentLight = true;
        }


        public override void Reset()
        {
            base.Reset();

            foreach (var lightShaderGroup in lightShaderGroupsPerVoxel)
                lightShaderGroup.Value.Reset();

            lightShaderGroupsPerVoxel.Clear();
            pool.Reset();
        }

        /// <inheritdoc/>
        public override void ProcessLights(ProcessLightsParameters parameters)
        {
            foreach (var index in parameters.LightIndices)
            {
                // For now, we allow only one cubemap at once
                var light = parameters.LightCollection[index];

                // Prepare LightVoxelShaderGroup
                LightVoxelShaderGroup lightShaderGroup;
                if (!lightShaderGroupsPerVoxel.TryGetValue(light, out lightShaderGroup))
                {
                    lightShaderGroup = pool.Add();
                    lightShaderGroup.Light = light;

                    lightShaderGroupsPerVoxel.Add(light, lightShaderGroup);
                }
            }

            // Consume all the lights
            parameters.LightIndices.Clear();
        }

        public override void UpdateShaderPermutationEntry(ForwardLightingRenderFeature.LightShaderPermutationEntry shaderEntry)
        {
            foreach (var cubemap in lightShaderGroupsPerVoxel)
            {
                shaderEntry.EnvironmentLights.Add(cubemap.Value);
            }
        }

        private static LightVoxelShaderGroup CreateLightVoxelShaderGroup()
        {
            return new LightVoxelShaderGroup(new ShaderMixinGeneratorSource("LightVoxelEffect"));
        }

        private class LightVoxelShaderGroup : LightShaderGroup
        {
            private ValueParameterKey<float> intensityKey;
            private ValueParameterKey<float> specularIntensityKey;
            private ValueParameterKey<float> skyIntensityKey;
            private ValueParameterKey<float> skySpecularIntensityKey;
            private ValueParameterKey<float> skyMipCountKey;
            private ValueParameterKey<Color3> skyColorsKey;
            private ObjectParameterKey<Texture> skyCubeMapKey;

            private PermutationParameterKey<ShaderSource> diffuseMarcherKey;
            private PermutationParameterKey<ShaderSource> specularMarcherKey;

            public RenderLight Light { get; set; }

            VoxelAttribute traceAttribute = null;

            public LightVoxelShaderGroup(ShaderSource mixin) : base(mixin)
            {
                HasEffectPermutations = true;
                traceAttribute = null;
            }

            ProcessedVoxelVolume GetProcessedVolume()
            {
                var lightVoxel = ((LightVoxel)Light.Type);
                if (lightVoxel.Volume == null)
                {
                    throw new ArgumentNullException("No Voxel Volume Component selected for voxel light.");
                }
                var voxelVolumeProcessor = lightVoxel.Volume.Entity.EntityManager.GetProcessor<VoxelVolumeProcessor>();
                if (voxelVolumeProcessor == null)
                    return null;

                ProcessedVoxelVolume processedVolume = voxelVolumeProcessor.GetProcessedVolumeForComponent(lightVoxel.Volume);
                return processedVolume;
            }

            VoxelAttribute GetTraceAttr()
            {
                var lightVoxel = ((LightVoxel)Light.Type);

                ProcessedVoxelVolume processedVolume = GetProcessedVolume();
                if (processedVolume == null)
                    return null;

                if (processedVolume.OutputAttributes.Count > lightVoxel.AttributeIndex)
                {
                    return processedVolume.OutputAttributes[lightVoxel.AttributeIndex];
                }
                else
                {
                    throw new ArgumentOutOfRangeException("Tried to access attribute index " + lightVoxel.AttributeIndex.ToString() + " (zero-indexed) when the Voxel Volume Component has only " + processedVolume.OutputAttributes.Count.ToString() + " attributes.");
                }
            }
            public override void UpdateLayout(string compositionName)
            {
                base.UpdateLayout(compositionName);

                traceAttribute = GetTraceAttr();

                intensityKey = LightVoxelShaderKeys.Intensity.ComposeWith(compositionName);
                specularIntensityKey = LightVoxelShaderKeys.SpecularIntensity.ComposeWith(compositionName);
                skyIntensityKey = LightVoxelShaderKeys.SkyIntensity.ComposeWith(compositionName);
                skySpecularIntensityKey = LightVoxelShaderKeys.SkySpecularIntensity.ComposeWith(compositionName);
                skyMipCountKey = LightVoxelShaderKeys.SkyMipCount.ComposeWith(compositionName);
                skyColorsKey = LightVoxelShaderKeys.SkyColors.ComposeWith(compositionName);
                skyCubeMapKey = LightVoxelShaderKeys.SkyCubeMap.ComposeWith(compositionName);

                diffuseMarcherKey = LightVoxelShaderKeys.diffuseMarcher.ComposeWith(compositionName);
                specularMarcherKey = LightVoxelShaderKeys.specularMarcher.ComposeWith(compositionName);

                if (traceAttribute != null)
                {
                    if (((LightVoxel)Light.Type).DiffuseMarcher != null)
                        ((LightVoxel)Light.Type).DiffuseMarcher.UpdateMarchingLayout("diffuseMarcher." + compositionName);
                    if (((LightVoxel)Light.Type).SpecularMarcher != null)
                        ((LightVoxel)Light.Type).SpecularMarcher.UpdateMarchingLayout("specularMarcher." + compositionName);
                }
            }

            public override void ApplyEffectPermutations(RenderEffect renderEffect)
            {
                if (traceAttribute != null)
                {
                    ShaderSourceCollection collection = new ShaderSourceCollection
                    {
                        traceAttribute.GetSamplingShader()
                    };

                    if (((LightVoxel)Light.Type).DiffuseMarcher != null)
                        renderEffect.EffectValidator.ValidateParameter(diffuseMarcherKey, ((LightVoxel)Light.Type).DiffuseMarcher.GetMarchingShader(0, collection));
                    if (((LightVoxel)Light.Type).SpecularMarcher != null)
                        renderEffect.EffectValidator.ValidateParameter(specularMarcherKey, ((LightVoxel)Light.Type).SpecularMarcher.GetMarchingShader(0, collection));
                }
            }

            public override void ApplyViewParameters(RenderDrawContext context, int viewIndex, ParameterCollection parameters)
            {
                base.ApplyViewParameters(context, viewIndex, parameters);

                var lightVoxel = ((LightVoxel)Light.Type);

                if (lightVoxel.Volume == null)
                    return;
                ProcessedVoxelVolume processedVolume = GetProcessedVolume();
                if (processedVolume == null)
                    return;

                var intensity = Light.Intensity;
                var intensityBounceScale = lightVoxel.BounceIntensityScale;
                var specularIntensity = lightVoxel.SpecularIntensityScale * intensity;

                // KotH patch: kept passes hold last voxelisation's views, whose index may be any view's now.
                VoxelViewContext viewContext = processedVolume.Voxelize ? new VoxelViewContext(processedVolume.passList, viewIndex) : new VoxelViewContext(false);
                if (viewContext.IsVoxelView)
                {
                    intensity *= intensityBounceScale / 3.141592f;
                    specularIntensity = 0.0f;
                }

                parameters.Set(intensityKey, intensity);
                parameters.Set(specularIntensityKey, specularIntensity);

                // KotH patch: voxels take the sky unoccluded (no cones there) and no reflection.
                var sky = lightVoxel.Sky;
                var skyCube = sky?.SpecularLightingParameters.Get(SkyboxKeys.CubeMap);
                var skyIntensity = sky != null ? lightVoxel.SkyIntensity : 0.0f;
                parameters.Set(skyIntensityKey, skyIntensity);
                parameters.Set(skySpecularIntensityKey, viewContext.IsVoxelView || skyCube == null ? 0.0f : skyIntensity);
                parameters.Set(skyMipCountKey, skyCube?.MipLevelCount ?? 0);
                parameters.Set(skyCubeMapKey, skyCube);
                if (sky != null)
                    sky.DiffuseLightingParameters.CopyTo(SphericalHarmonicsEnvironmentColorKeys.SphericalColors, parameters, skyColorsKey);

                if (traceAttribute != null)
                {
                    lightVoxel.DiffuseMarcher?.ApplyMarchingParameters(parameters);
                    lightVoxel.SpecularMarcher?.ApplyMarchingParameters(parameters);
                    lightVoxel.DiffuseMarcher?.ApplyAttributeSamplers(traceAttribute, 0, viewContext, parameters);
                    lightVoxel.SpecularMarcher?.ApplyAttributeSamplers(traceAttribute, 0, viewContext, parameters);
                }
            }
        }
    }
}

