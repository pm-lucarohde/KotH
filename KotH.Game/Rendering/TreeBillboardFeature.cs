using System.Collections.Generic;
using System.Linq;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Rendering;
using Stride.Shaders;

namespace KotH.Client.Rendering
{
    /// <summary>Mixes TreeBillboard into every pass of the far trees (Pyramid's forest outside the wall:
    /// the material Materials/Pyramid/FarTrees), after the vertex stage as LeafRenderFeature does for the leaves,
    /// so each flat tree turns to the camera. Added at run time; the material is the loaded map's.</summary>
    public sealed class TreeBillboardFeature : SubRenderFeature
    {
        static readonly ShaderClassSource Turn = new ShaderClassSource("TreeBillboard");

        /// <summary>The loaded map's far-tree material, null on a map without.</summary>
        public static Material Material { get; private set; }

        // Its asset's url, after the package's name (/KotH.Game/...).
        const string Url = "/Materials/Pyramid/FarTrees";

        /// <summary>Finds the far trees' material among the map's (by its asset) and hooks the feature into the
        /// compositor once.</summary>
        public static void Install(SceneSystem scene, ContentManager content, IEnumerable<Entity> map)
        {
            Material = map.SelectMany(Tree).SelectMany(e => e.GetAll<ModelComponent>())
                .SelectMany(m => m.Model?.Materials ?? Enumerable.Empty<MaterialInstance>())
                .Select(m => m.Material)
                .FirstOrDefault(m => m != null && content.TryGetAssetUrl(m, out string url) && url.EndsWith(Url));
            StartLog.Mark(Material != null ? "far trees turn to the camera" : "no far trees on this map");
            if (Material == null)
                return;
            var meshes = scene.GraphicsCompositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            if (meshes != null && !meshes.RenderFeatures.OfType<TreeBillboardFeature>().Any())
                meshes.RenderFeatures.Add(new TreeBillboardFeature());
        }

        // An entity and all below it (the map's models sit on the prefab's children).
        static IEnumerable<Entity> Tree(Entity e) => e.Transform.Children.SelectMany(c => Tree(c.Entity)).Prepend(e);

        /// <summary>Forgets the unloaded map's material.</summary>
        public static void Clear() => Material = null;

        /// <summary>A mesh drawn with the far trees' material.</summary>
        internal static bool IsBillboard(RenderMesh mesh) => Material != null && mesh.MaterialPass?.Material == Material;

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            if (Material == null)
                return;
            var root = (RootEffectRenderFeature)RootRenderFeature;
            var effects = root.RenderData.GetData(root.RenderEffectKey);
            int slots = root.EffectPermutationSlotCount;
            foreach (RenderObject renderObject in root.RenderObjects)
            {
                if (renderObject is not RenderMesh mesh || !IsBillboard(mesh))
                    continue;
                for (int i = 0; i < slots; i++)
                {
                    RenderEffect effect = effects[renderObject.StaticObjectNode * slots + i];
                    if (effect != null && effect.IsUsedDuringThisFrame(RenderSystem))
                        effect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ExtensionPostVertexStageShader, Turn);
                }
            }
        }
    }
}
