using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KotH.Client.Rendering.Dlss;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Vortice.Vulkan;
using Buffer = Stride.Graphics.Buffer;

namespace KotH.Client.Rendering
{
    /// <summary>Culls parts of the map's big meshes in the camera's depth prepass and colour pass. At load every such
    /// mesh's triangles are sorted into clusters (cubes of Cell metres, and where the mesh does not sway, by facing);
    /// each frame the camera keeps the clusters in its frustum that are not wholly turned away, and the mesh's one
    /// draw becomes a draw per run of kept clusters (vkCmdDrawIndexed swapped). The GPU would drop the rest anyway.</summary>
    public static unsafe class MapCulling
    {
        /// <summary>Edge of a cluster's cube in metres; 0 leaves the meshes whole.</summary>
        public static float Cell = 32f;

        const int MinTriangles = 20000;
        // How far the leaves' wind moves a vertex at most (LeafSway: 0.059 m x 2.5 + flap 0.025), with room.
        const float SwayMargin = 0.25f;
        // Frustum room for the DLSS jitter and rounding.
        const float Margin = 0.1f;

        struct Cluster
        {
            public uint First, Count;
            public Vector3 Centre, Half;
            public Vector3 Axis;
            // Largest angle between a triangle's facing and Axis; negative: no facing test.
            public float Spread;
        }

        sealed class Cut
        {
            public VkBuffer Indices;
            public uint Total;
            public Cluster[] Clusters;
        }

        sealed class Visible
        {
            public Cut[] For;
            public uint[][] Runs;
            public int[] Counts;
            public int Frame;
        }

        static Cut[] cuts = [];
        static readonly List<Buffer> owned = new List<Buffer>();
        static readonly Dictionary<RenderView, Visible> views = new Dictionary<RenderView, Visible>();
        static int frame;

        [ThreadStatic] static Visible current;
        [ThreadStatic] static VkCommandBuffer boundCommands;
        [ThreadStatic] static VkBuffer boundIndices;

        static delegate* unmanaged<VkCommandBuffer, VkBuffer, ulong, VkIndexType, void> bindIndexBuffer;
        static delegate* unmanaged<VkCommandBuffer, uint, uint, uint, int, uint, void> drawIndexed;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeBuffer")]
        static extern ref VkBuffer NativeBuffer(Buffer buffer);

        /// <summary>Swaps the device's index buffer binding and indexed draw; once, right after the device is made.</summary>
        public static void Install(GraphicsDevice device)
        {
            if (drawIndexed != null)
                return;
            VkDeviceApi api = StrideVulkan.DeviceApi(device);
            static void* Swap(in PFN_vkVoidFunction field, void* hook)
            {
                void* original = (void*)field.Value;
                Unsafe.AsRef(in field) = new PFN_vkVoidFunction((delegate* unmanaged<void>)hook);
                return original;
            }
            bindIndexBuffer = (delegate* unmanaged<VkCommandBuffer, VkBuffer, ulong, VkIndexType, void>)
                Swap(api.vkCmdBindIndexBuffer_ptr, (delegate* unmanaged<VkCommandBuffer, VkBuffer, ulong, VkIndexType, void>)&BindIndexBuffer);
            drawIndexed = (delegate* unmanaged<VkCommandBuffer, uint, uint, uint, int, uint, void>)
                Swap(api.vkCmdDrawIndexed_ptr, (delegate* unmanaged<VkCommandBuffer, uint, uint, uint, int, uint, void>)&DrawIndexed);
        }

        /// <summary>Hooks the culling into the scene's compositor once.</summary>
        public static void InstallFeature(SceneSystem scene)
        {
            var meshes = scene.GraphicsCompositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
            if (meshes != null && !meshes.RenderFeatures.OfType<Feature>().Any())
                meshes.RenderFeatures.Add(new Feature());
        }

        /// <summary>Sorts the big meshes of the models under these entities into clusters; returns how many.</summary>
        public static int Split(IEnumerable<Entity> entities, IServiceRegistry services, GraphicsDevice device)
        {
            if (Cell <= 0f || drawIndexed == null)
                return 0;
            var made = new List<Cut>(cuts);
            var vertexData = new Dictionary<Buffer, byte[]>();
            var stack = new Stack<Entity>(entities);
            int clusters = 0;
            while (stack.Count > 0)
            {
                Entity e = stack.Pop();
                foreach (TransformComponent child in e.Transform.Children)
                    stack.Push(child.Entity);
                if (e.Get<ModelComponent>()?.Model is not { } model)
                    continue;
                foreach (Mesh mesh in model.Meshes)
                    if (Big(mesh) && Build(mesh, vertexData, services, device) is { } cut)
                    {
                        made.Add(cut);
                        clusters += cut.Clusters.Length;
                    }
            }
            cuts = made.ToArray();
            return clusters;
        }

        /// <summary>Forgets the map's clusters and frees their index buffers (with the map).</summary>
        public static void Clear()
        {
            cuts = [];
            views.Clear();
            foreach (Buffer buffer in owned)
                buffer.Dispose();
            owned.Clear();
        }

        // A big static triangle mesh spanning the map.
        static bool Big(Mesh mesh)
        {
            MeshDraw draw = mesh.Draw;
            Vector3 size = mesh.BoundingBox.Maximum - mesh.BoundingBox.Minimum;
            return draw?.IndexBuffer != null && draw.PrimitiveType == PrimitiveType.TriangleList && draw.VertexBuffers.Length > 0
                   && draw.DrawCount / 3 >= MinTriangles && mesh.Skinning == null && Math.Max(size.X, Math.Max(size.Y, size.Z)) > 64f;
        }

        static Cut Build(Mesh mesh, Dictionary<Buffer, byte[]> vertexData, IServiceRegistry services, GraphicsDevice device)
        {
            MeshDraw draw = mesh.Draw;
            Vector3 size = mesh.BoundingBox.Maximum - mesh.BoundingBox.Minimum;
            VertexBufferBinding vertices = draw.VertexBuffers[0];
            if (!vertices.Declaration.TryGetElement("POSITION", 0, out VertexElementWithOffset position)
                || position.VertexElement.Format != PixelFormat.R32G32B32_Float)
                return null;
            bool hasNormal = vertices.Declaration.TryGetElement("NORMAL", 0, out VertexElementWithOffset normal)
                             && normal.VertexElement.Format == PixelFormat.R32G32B32_Float;
            // Only what never moves gets the facing test: the leaves sway in the vertex shader.
            bool still = vertices.Declaration.TryGetElement("TEXCOORD", 1, out _);
            if (!vertexData.TryGetValue(vertices.Buffer, out byte[] bytes))
            {
                vertices.AsReadable(services, out VertexBufferHelper helper, out _);
                vertexData[vertices.Buffer] = bytes = helper.DataOuter;
            }
            draw.IndexBuffer.AsReadable(services, out IndexBufferHelper indexHelper, out _);
            int[] indices = indexHelper.To32Bit().Slice(draw.StartLocation, draw.DrawCount).ToArray();
            int stride = vertices.Stride, at = vertices.Offset + position.Offset, nat = vertices.Offset + normal.Offset;
            Vector3 P(int v) => MemoryMarshal.Read<Vector3>(bytes.AsSpan(at + v * stride));
            Vector3 N(int v) => MemoryMarshal.Read<Vector3>(bytes.AsSpan(nat + v * stride));

            // The facing the GPU keeps: the winding's normal, turned to agree with most vertex normals.
            int triangles = indices.Length / 3;
            var facing = new Vector3[triangles];
            int agree = 0;
            for (int t = 0; t < triangles; t++)
            {
                Vector3 a = P(indices[3 * t]), b = P(indices[3 * t + 1]), c = P(indices[3 * t + 2]);
                Vector3 n = Vector3.Cross(b - a, c - a);
                float length = n.Length();
                facing[t] = length > 0f ? n / length : Vector3.Zero;
                if (hasNormal && Vector3.Dot(n, N(indices[3 * t]) + N(indices[3 * t + 1]) + N(indices[3 * t + 2])) > 0f)
                    agree++;
            }
            float sign = agree * 2 >= triangles ? 1f : -1f;
            // Facing only where the normals tell it clearly.
            still &= hasNormal && Math.Max(agree, triangles - agree) >= 0.98f * triangles;

            // The key of every triangle: its cube (by its centre), then its dominant facing axis.
            Vector3 min = mesh.BoundingBox.Minimum;
            int nx = (int)(size.X / Cell) + 1, ny = (int)(size.Y / Cell) + 1, nz = (int)(size.Z / Cell) + 1;
            int bins = still ? 6 : 1;
            var key = new int[triangles];
            var counts = new int[nx * ny * nz * bins];
            for (int t = 0; t < triangles; t++)
            {
                Vector3 c = (P(indices[3 * t]) + P(indices[3 * t + 1]) + P(indices[3 * t + 2])) / 3f - min;
                int x = Math.Clamp((int)(c.X / Cell), 0, nx - 1), y = Math.Clamp((int)(c.Y / Cell), 0, ny - 1);
                int z = Math.Clamp((int)(c.Z / Cell), 0, nz - 1);
                facing[t] *= sign;
                int bin = 0;
                if (still)
                {
                    Vector3 f = facing[t], m = new Vector3(Math.Abs(f.X), Math.Abs(f.Y), Math.Abs(f.Z));
                    bin = m.X >= m.Y && m.X >= m.Z ? (f.X >= 0 ? 0 : 1) : m.Y >= m.Z ? (f.Y >= 0 ? 2 : 3) : (f.Z >= 0 ? 4 : 5);
                }
                counts[key[t] = ((x * ny + y) * nz + z) * bins + bin]++;
            }

            // Triangles in key order (stable inside a cluster); per cluster its box and facing cone.
            var first = new int[counts.Length + 1];
            for (int k = 0; k < counts.Length; k++)
                first[k + 1] = first[k] + counts[k];
            var next = (int[])first.Clone();
            var sorted = new int[indices.Length];
            var order = new int[triangles];
            for (int t = 0; t < triangles; t++)
            {
                int to = next[key[t]]++;
                order[to] = t;
                sorted[3 * to] = indices[3 * t];
                sorted[3 * to + 1] = indices[3 * t + 1];
                sorted[3 * to + 2] = indices[3 * t + 2];
            }
            float room = still ? Margin : SwayMargin;
            var clusters = new List<Cluster>();
            for (int k = 0; k < counts.Length; k++)
            {
                if (counts[k] == 0)
                    continue;
                BoundingBox box = BoundingBox.Empty;
                Vector3 sum = Vector3.Zero;
                for (int s = first[k]; s < first[k + 1]; s++)
                {
                    for (int v = 0; v < 3; v++)
                    {
                        Vector3 p = P(sorted[3 * s + v]);
                        BoundingBox.Merge(ref box, ref p, out box);
                    }
                    sum += facing[order[s]];
                }
                float spread = -1f;
                Vector3 axis = Vector3.Zero;
                if (still && sum.Length() > 0f)
                {
                    axis = Vector3.Normalize(sum);
                    float least = 1f;
                    for (int s = first[k]; s < first[k + 1]; s++)
                        least = Math.Min(least, facing[order[s]] == Vector3.Zero ? -1f : Vector3.Dot(facing[order[s]], axis));
                    if (least > 0.1f)
                        spread = MathF.Acos(least);
                }
                clusters.Add(new Cluster
                {
                    First = (uint)(3 * first[k]), Count = (uint)(3 * counts[k]),
                    Centre = box.Center, Half = box.Extent + new Vector3(room), Axis = axis, Spread = spread,
                });
            }

            Buffer buffer = Buffer.Index.New(device, sorted, GraphicsResourceUsage.Immutable);
            owned.Add(buffer);
            mesh.Draw = new MeshDraw
            {
                PrimitiveType = draw.PrimitiveType, VertexBuffers = draw.VertexBuffers,
                IndexBuffer = new IndexBufferBinding(buffer, true, sorted.Length), StartLocation = 0, DrawCount = sorted.Length,
            };
            return new Cut { Indices = NativeBuffer(buffer), Total = (uint)sorted.Length, Clusters = clusters.ToArray() };
        }

        // Once a frame, before the draws: the runs of kept clusters of every view that has the camera's passes.
        static void Prepare(RenderSystem system, int opaque, int prepass)
        {
            frame++;
            Cut[] all = cuts;
            if (all.Length == 0)
                return;
            foreach (RenderView view in system.Views)
            {
                bool camera = false;
                foreach (RenderViewStage stage in view.RenderStages)
                    camera |= stage.Index == opaque || stage.Index == prepass;
                if (!camera)
                    continue;
                if (!views.TryGetValue(view, out Visible visible) || visible.For != all)
                {
                    visible = new Visible { For = all, Runs = new uint[all.Length][], Counts = new int[all.Length] };
                    for (int i = 0; i < all.Length; i++)
                        visible.Runs[i] = new uint[2 * all[i].Clusters.Length];
                    views[view] = visible;
                }
                visible.Frame = frame;
                Cull(view, all, visible);
            }
        }

        static void Cull(RenderView view, Cut[] all, Visible visible)
        {
            Matrix viewProjection = view.ViewProjection;
            var frustum = new BoundingFrustum(ref viewProjection);
            Vector3 eye = Matrix.Invert(view.View).TranslationVector;
            for (int i = 0; i < all.Length; i++)
            {
                Cluster[] clusters = all[i].Clusters;
                uint[] runs = visible.Runs[i];
                int n = 0;
                for (int k = 0; k < clusters.Length; k++)
                {
                    ref Cluster c = ref clusters[k];
                    var box = new BoundingBoxExt { Center = c.Centre, Extent = c.Half };
                    if (!frustum.Contains(ref box) || TurnedAway(ref c, eye))
                        continue;
                    if (n > 0 && runs[2 * n - 2] + runs[2 * n - 1] == c.First)
                        runs[2 * n - 1] += c.Count;
                    else
                    {
                        runs[2 * n] = c.First;
                        runs[2 * n + 1] = c.Count;
                        n++;
                    }
                }
                visible.Counts[i] = n;
            }
        }

        // Every triangle of the cluster faces away from the eye: for p in its sphere and a facing n within Spread
        // of Axis, dot(n, p - eye) >= |v| cos(angle(v, Axis) + Spread) - R > 0, with v = Centre - eye.
        static bool TurnedAway(ref Cluster c, Vector3 eye)
        {
            if (c.Spread < 0f)
                return false;
            Vector3 v = c.Centre - eye;
            float length = v.Length();
            float radius = c.Half.Length();
            if (length <= radius)
                return false;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(v, c.Axis) / length, -1f, 1f)) + c.Spread;
            return angle < MathF.PI / 2f && length * MathF.Cos(angle) > radius;
        }

        [UnmanagedCallersOnly]
        static void BindIndexBuffer(VkCommandBuffer commands, VkBuffer buffer, ulong offset, VkIndexType type)
        {
            boundCommands = commands;
            boundIndices = offset == 0 ? buffer : VkBuffer.Null;
            bindIndexBuffer(commands, buffer, offset, type);
        }

        [UnmanagedCallersOnly]
        static void DrawIndexed(VkCommandBuffer commands, uint count, uint instances, uint firstIndex, int vertexOffset, uint firstInstance)
        {
            Visible visible = current;
            if (visible != null && firstIndex == 0 && instances == 1 && vertexOffset == 0 && commands == boundCommands)
            {
                Cut[] all = visible.For;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Indices != boundIndices || all[i].Total != count)
                        continue;
                    uint[] runs = visible.Runs[i];
                    for (int r = 0, n = visible.Counts[i]; r < n; r++)
                        drawIndexed(commands, runs[2 * r + 1], 1, runs[2 * r], 0, 0);
                    return;
                }
            }
            drawIndexed(commands, count, instances, firstIndex, vertexOffset, firstInstance);
        }

        /// <summary>Hands each draw of the camera's passes its view's kept clusters (on the thread that records).</summary>
        sealed class Feature : SubRenderFeature
        {
            int opaque = -2, prepass = -2;

            public override void Prepare(RenderDrawContext context)
            {
                if (opaque == -2)
                {
                    opaque = prepass = -1;
                    foreach (RenderStage stage in RenderSystem.RenderStages)
                        if (stage.Name == "Opaque")
                            opaque = stage.Index;
                        else if (stage.Name == "GBuffer")
                            prepass = stage.Index;
                }
                MapCulling.Prepare(RenderSystem, opaque, prepass);
            }

            public override void Draw(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage, int startIndex, int endIndex)
            {
                current = (renderViewStage.Index == opaque || renderViewStage.Index == prepass)
                          && views.TryGetValue(renderView, out Visible visible) && visible.Frame == frame ? visible : null;
            }
        }
    }
}
