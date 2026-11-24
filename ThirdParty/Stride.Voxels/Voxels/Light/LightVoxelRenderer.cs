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
            // KotH patch: Clear keeps the groups for the next frame (Reset dropped them, so every frame
            // made a new group, new marcher shaders and every object's effect validated deep, never equal).
            pool.Clear();
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
            // KotH patch: capsules for what the voxels do not hold.
            private ValueParameterKey<Vector4> occludersKey;
            private ValueParameterKey<Vector4> occluderGroupsKey;
            private ValueParameterKey<Vector4> occluderGlowsKey;
            private ValueParameterKey<int> occluderGroupCountKey;
            private ValueParameterKey<int> occluderGroupSizeKey;
            private ValueParameterKey<float> occluderFadeKey;
            private ObjectParameterKey<Texture> lightmapKey; // KotH patch

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

                intensityKey = VoxelNames.Compose(LightVoxelShaderKeys.Intensity, compositionName);
                specularIntensityKey = VoxelNames.Compose(LightVoxelShaderKeys.SpecularIntensity, compositionName);
                skyIntensityKey = VoxelNames.Compose(LightVoxelShaderKeys.SkyIntensity, compositionName);
                skySpecularIntensityKey = VoxelNames.Compose(LightVoxelShaderKeys.SkySpecularIntensity, compositionName);
                skyMipCountKey = VoxelNames.Compose(LightVoxelShaderKeys.SkyMipCount, compositionName);
                skyColorsKey = VoxelNames.Compose(LightVoxelShaderKeys.SkyColors, compositionName);
                skyCubeMapKey = VoxelNames.Compose(LightVoxelShaderKeys.SkyCubeMap, compositionName);
                occludersKey = VoxelNames.Compose(LightVoxelShaderKeys.Occluders, compositionName);
                occluderGroupsKey = VoxelNames.Compose(LightVoxelShaderKeys.OccluderGroups, compositionName);
                occluderGlowsKey = VoxelNames.Compose(LightVoxelShaderKeys.OccluderGlows, compositionName);
                occluderGroupCountKey = VoxelNames.Compose(LightVoxelShaderKeys.OccluderGroupCount, compositionName);
                occluderGroupSizeKey = VoxelNames.Compose(LightVoxelShaderKeys.OccluderGroupSize, compositionName);
                occluderFadeKey = VoxelNames.Compose(LightVoxelShaderKeys.OccluderFade, compositionName);
                lightmapKey = VoxelNames.Compose(LightVoxelShaderKeys.Lightmap, compositionName);

                diffuseMarcherKey = VoxelNames.Compose(LightVoxelShaderKeys.diffuseMarcher, compositionName);
                specularMarcherKey = VoxelNames.Compose(LightVoxelShaderKeys.specularMarcher, compositionName);

                if (traceAttribute != null)
                {
                    if (((LightVoxel)Light.Type).DiffuseMarcher != null)
                        ((LightVoxel)Light.Type).DiffuseMarcher.UpdateMarchingLayout(VoxelNames.Join("diffuseMarcher.", compositionName));
                    if (((LightVoxel)Light.Type).SpecularMarcher != null)
                        ((LightVoxel)Light.Type).SpecularMarcher.UpdateMarchingLayout(VoxelNames.Join("specularMarcher.", compositionName));
                }

                // KotH patch: built here (before the objects' permutations run in parallel), once per name,
                // attribute and light, so each object's validator sees the same instance (no deep compare).
                if (compositionName == builtFor && traceAttribute == builtWith && Light.Type == builtLight)
                    return;
                (builtFor, builtWith, builtLight) = (compositionName, traceAttribute, Light.Type);
                ShaderSourceCollection collection = traceAttribute != null ? new ShaderSourceCollection { traceAttribute.GetSamplingShader() } : null;
                diffuseShader = Keep(diffuseShader, collection != null ? ((LightVoxel)Light.Type).DiffuseMarcher?.GetMarchingShader(0, collection) : null);
                specularShader = Keep(specularShader, collection != null ? ((LightVoxel)Light.Type).SpecularMarcher?.GetMarchingShader(0, collection) : null);
            }

            ShaderSource diffuseShader, specularShader;
            string builtFor;
            VoxelAttribute builtWith;
            object builtLight;

            static ShaderSource Keep(ShaderSource kept, ShaderSource built) => kept != null && kept.Equals(built) ? kept : built;

            public override void ApplyEffectPermutations(RenderEffect renderEffect)
            {
                if (diffuseShader != null)
                    renderEffect.EffectValidator.ValidateParameter(diffuseMarcherKey, diffuseShader);
                if (specularShader != null)
                    renderEffect.EffectValidator.ValidateParameter(specularMarcherKey, specularShader);
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

                // KotH patch: the capsules darken only what the camera sees, never the voxels.
                int groupSize = Math.Clamp(lightVoxel.OccluderGroupSize, 0, LightVoxel.MaxOccludersPerGroup);
                int groups = viewContext.IsVoxelView || groupSize == 0 ? 0 : Math.Clamp(lightVoxel.OccluderGroupCount, 0, LightVoxel.MaxOccluderGroups);
                parameters.Set(occluderGroupCountKey, groups);
                parameters.Set(occluderGroupSizeKey, groupSize);
                parameters.Set(occluderFadeKey, lightVoxel.OccluderFade);
                if (groups > 0)
                {
                    parameters.Set(occluderGroupsKey, groups, ref lightVoxel.OccluderGroups[0]);
                    parameters.Set(occluderGlowsKey, groups, ref lightVoxel.OccluderGlows[0]);
                    parameters.Set(occludersKey, 2 * groups * groupSize, ref lightVoxel.Occluders[0]);
                }

                // KotH patch: the baked light for meshes with a second UV set.
                if (lightVoxel.Lightmap != null)
                    parameters.Set(lightmapKey, lightVoxel.Lightmap);

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

