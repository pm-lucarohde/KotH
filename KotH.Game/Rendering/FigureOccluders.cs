using System;
using KotH.Client.Player;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Voxels.VoxelGI;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Hands the figures to the voxel light as capsules, every frame, instead
    /// of voxelising them (Luca's choice, far cheaper): both legs, torso, cap
    /// and both forearms with their gloves, each hung on bones of the figure's
    /// skeleton, so they follow the clips.
    /// They darken the bounce and the sky round a figure. Runs after
    /// PlayerVisualSystem.
    /// </summary>
    /// <userdoc>Gibt die Figuren als Kapseln an das Voxel-Licht (weicher Schatten im indirekten Licht).</userdoc>
    public sealed class FigureOccluders : SyncScript
    {
        // Ends in the figure's rest pose (model space, standing, centre at 0),
        // each on a bone, and the radius; measured from Mushroom.glb.
        readonly record struct Part(string BoneA, Vector3 A, string BoneB, Vector3 B, float Radius);

        static readonly Part[] Parts =
        {
            new("UpperLeg.L", new(-0.14f, -0.6f, -0.09f), "LowerLeg.L", new(-0.155f, -0.84f, -0.11f), 0.09f),
            new("UpperLeg.R", new(0.08f, -0.6f, -0.11f), "LowerLeg.R", new(0.085f, -0.84f, -0.13f), 0.09f),
            new("Hips", new(-0.01f, -0.36f, -0.12f), "Chest", new(0f, 0.05f, -0.1f), 0.27f),
            new("Head", new(0f, 0.42f, 0f), "Head", new(0f, 0.55f, 0f), 0.43f),
            new("LowerArmSwing.R", new(0.3f, -0.1f, -0.15f), "Grip.R", new(0.32f, -0.38f, -0.16f), 0.09f),
            new("LowerArm.L", new(-0.32f, -0.08f, -0.03f), "LowerArm.L", new(-0.36f, -0.36f, -0.05f), 0.09f),
        };

        /// <summary>A part on one skeleton: bone indices and the ends in their bones' frames.</summary>
        readonly record struct Hung(int BoneA, Vector3 A, int BoneB, Vector3 B, float Radius);

        // Every figure is the same model, so the parts hang the same on each.
        Hung[] hung;
        PlayerVisualSystem visuals;

        public FigureOccluders()
        {
            Priority = 1000;
        }

        public override void Update()
        {
            if (Entity.Get<LightComponent>()?.Type is not LightVoxel light)
                return;
            visuals ??= Find();
            light.OccluderGroupSize = Parts.Length;
            int groups = 0;
            if (visuals != null)
            {
                foreach (Entity figure in visuals.Figures)
                {
                    if (groups == LightVoxel.MaxOccluderGroups)
                        break;
                    if (figure.Get<ModelComponent>()?.Skeleton is not { } skeleton)
                        continue;
                    Fill(light, groups++, figure.Transform, skeleton);
                }
            }
            light.OccluderGroupCount = groups;
        }

        /// <summary>
        /// Group g from the skeleton's last pose (its matrices are a frame old,
        /// in world space through node 0), moved to where the figure is now.
        /// </summary>
        void Fill(LightVoxel light, int g, TransformComponent t, SkeletonUpdater skeleton)
        {
            Hung[] parts = hung ??= Hang(skeleton);
            ModelNodeTransformation[] nodes = skeleton.NodeTransformations;
            Matrix.Invert(ref nodes[0].WorldMatrix, out Matrix shown);
            Matrix.Transformation(ref t.Scale, ref t.Rotation, ref t.Position, out Matrix now);
            Matrix toNow = shown * now;
            float reach = 0f;
            for (int k = 0; k < parts.Length; k++)
            {
                Hung part = parts[k];
                Vector3 a = Vector3.TransformCoordinate(Vector3.TransformCoordinate(part.A, nodes[part.BoneA].WorldMatrix), toNow);
                Vector3 b = Vector3.TransformCoordinate(Vector3.TransformCoordinate(part.B, nodes[part.BoneB].WorldMatrix), toNow);
                float r = part.Radius * t.Scale.X;
                int i = 2 * (g * parts.Length + k);
                light.Occluders[i] = new Vector4(a, r);
                light.Occluders[i + 1] = new Vector4(b, 0f);
                reach = Math.Max(reach, Math.Max((a - t.Position).Length(), (b - t.Position).Length()) + light.OccluderFade * r);
            }
            light.OccluderGroups[g] = new Vector4(t.Position, reach);
        }

        /// <summary>Every part's ends in its bones' frames, from the rest pose.</summary>
        static Hung[] Hang(SkeletonUpdater skeleton)
        {
            ModelNodeDefinition[] nodes = skeleton.Nodes;
            var rest = new Matrix[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                TransformTRS trs = nodes[i].Transform;
                Matrix.Transformation(ref trs.Scale, ref trs.Rotation, ref trs.Position, out Matrix local);
                rest[i] = nodes[i].ParentIndex >= 0 ? local * rest[nodes[i].ParentIndex] : local;
            }
            var parts = new Hung[Parts.Length];
            for (int k = 0; k < Parts.Length; k++)
            {
                Part part = Parts[k];
                int a = Bone(nodes, part.BoneA), b = Bone(nodes, part.BoneB);
                parts[k] = new Hung(a, Vector3.TransformCoordinate(part.A, Matrix.Invert(rest[a])),
                    b, Vector3.TransformCoordinate(part.B, Matrix.Invert(rest[b])), part.Radius);
            }
            return parts;
        }

        static int Bone(ModelNodeDefinition[] nodes, string name)
        {
            for (int i = 0; i < nodes.Length; i++)
                if (nodes[i].Name == name)
                    return i;
            throw new InvalidOperationException($"Knochen {name} fehlt");
        }

        PlayerVisualSystem Find()
        {
            if (SceneSystem.SceneInstance?.RootScene is not { } scene)
                return null;
            foreach (Entity e in scene.Entities)
                if (e.Get<PlayerVisualSystem>() is { } found)
                    return found;
            return null;
        }
    }
}
