using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;
using Stride.Shaders;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>DlssObjectVelocity for every mesh while DLSS runs: per draw the unjittered camera now
    /// and, for the point moved back by its entity's motion since the last frame, one frame ago. So
    /// figures, arms, cane and spores move right; bone animation and wind count as standing still.</summary>
    public sealed class DlssVelocityFeature : SubRenderFeature
    {
        static readonly ShaderClassSource Shader = new ShaderClassSource("DlssObjectVelocity");

        [StructLayout(LayoutKind.Sequential)]
        struct PerDraw
        {
            public Matrix CurrentClip;
            public Matrix PreviousClip;
        }

        ConstantBufferOffsetReference slot;
        readonly Dictionary<Entity, Matrix> previous = new Dictionary<Entity, Matrix>();
        readonly Dictionary<Entity, Matrix> current = new Dictionary<Entity, Matrix>();

        /// <summary>The renderer whose camera it uses; null or inactive, nothing is mixed in.</summary>
        internal static DlssRenderer Renderer { get; set; }

        /// <summary>Hooks it into the scene's compositor once.</summary>
        public static void Install(SceneSystem scene)
        {
            var meshes = scene.GraphicsCompositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            if (meshes != null && !meshes.RenderFeatures.OfType<DlssVelocityFeature>().Any())
                meshes.RenderFeatures.Add(new DlssVelocityFeature());
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();
            slot = ((RootEffectRenderFeature)RootRenderFeature).CreateDrawCBufferOffsetSlot("DlssObjectVelocity.CurrentClip");
        }

        static bool On => Renderer is { Active: true };

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            if (!On || Renderer.OpaqueStage == null)
                return;
            // Only the camera's opaque pass: shadows, depth prepass and voxels stay as they are.
            var root = (RootEffectRenderFeature)RootRenderFeature;
            var effects = root.RenderData.GetData(root.RenderEffectKey);
            int slots = root.EffectPermutationSlotCount;
            int slot = root.GetEffectPermutationSlot(Renderer.OpaqueStage).Index;
            foreach (RenderObject renderObject in root.RenderObjects)
            {
                if (renderObject is not RenderMesh)
                    continue;
                RenderEffect effect = effects[renderObject.StaticObjectNode * slots + slot];
                if (effect != null && effect.IsUsedDuringThisFrame(RenderSystem))
                    effect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ComputeVelocityShader, Shader);
            }
        }

        public override unsafe void Prepare(RenderDrawContext context)
        {
            if (!On)
            {
                previous.Clear();
                return;
            }
            Renderer.Clips(out Matrix now, out Matrix before);
            current.Clear();
            var root = (RootEffectRenderFeature)RootRenderFeature;
            foreach (RenderNode node in root.RenderNodes)
            {
                var layout = node.RenderEffect?.Reflection?.PerDrawLayout;
                int offset = layout?.GetConstantBufferOffset(slot) ?? -1;
                if (offset < 0 || node.RenderObject is not RenderMesh { Source: ModelComponent { Entity: { } entity } })
                    continue;
                if (!current.TryGetValue(entity, out Matrix world))
                    current[entity] = world = entity.Transform.WorldMatrix;
                // Where this point was one frame ago, if the entity carried it rigidly.
                Matrix back = previous.TryGetValue(entity, out Matrix last) && last != world
                    ? Matrix.Invert(world) * last * before : before;
                var data = (PerDraw*)((nint)node.Resources.ConstantBuffer.Data + offset);
                data->CurrentClip = now;
                data->PreviousClip = back;
            }
            previous.Clear();
            foreach (var (entity, world) in current)
                previous[entity] = world;
        }
    }
}
