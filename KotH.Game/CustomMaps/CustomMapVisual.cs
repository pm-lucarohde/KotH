using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KotH.MapFormat;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Buffer = Stride.Graphics.Buffer;

namespace KotH.Client.CustomMaps
{
    /// <summary>What a custom map package shows: map.glb's objects as models in the map's render group, one
    /// per material and shadow mode. Baked ones carry the lightmap UV, so they are lightmapped and cast once
    /// into the static sun map; live ones stay a model each and cast into a shadow tile fitted to them every
    /// frame (<see cref="Rendering.SunShadowMapRenderer"/>); off casts none. Materials copy the templates.</summary>
    [DataContract("KotH.CustomMapVisual")]
    [Display("Custom map visual")]
    public sealed class CustomMapVisual : StartupScript
    {
        public const RenderGroup Group = RenderGroup.Group3;

        /// <userdoc>Template of Surface and Terrain materials (MapSurface shader).</userdoc>
        [DataMember(10)] public Material Surface { get; set; }
        /// <userdoc>Template of Leaves materials: alpha cut at 0.5, both sides.</userdoc>
        [DataMember(11)] public Material Leaves { get; set; }
        /// <userdoc>Template of Water materials (the sea's waves).</userdoc>
        [DataMember(12)] public Material Water { get; set; }

        static readonly ValueParameterKey<Vector4> TintKey = ParameterKeys.NewValue<Vector4>(Vector4.One, "MapSurface.Tint");
        static readonly ValueParameterKey<float> TriplanarKey = ParameterKeys.NewValue<float>(0f, "MapSurface.Triplanar");
        static readonly ValueParameterKey<float> ScaleKey = ParameterKeys.NewValue<float>(1f, "MapSurface.TextureScale");
        static readonly ObjectParameterKey<Texture> ColorMapKey = ParameterKeys.NewObject<Texture>(null, "MapSurface.ColorMap");

        /// <summary>The drawn objects' bounds, for the light's voxel cube.</summary>
        public BoundingBox Bounds { get; private set; }

        /// <summary>How many objects, models and triangles were built.</summary>
        public (int Objects, int Models, int Triangles) Counts { get; private set; }

        readonly List<GraphicsResource> owned = new List<GraphicsResource>();

        [StructLayout(LayoutKind.Sequential)]
        struct Vertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv;
            public static readonly VertexDeclaration Layout = new VertexDeclaration(
                VertexElement.Position<Vector3>(), VertexElement.Normal<Vector3>(), VertexElement.TextureCoordinate<Vector2>(0));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LitVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv;
            public Vector2 LightUv;
            public static readonly VertexDeclaration Layout = new VertexDeclaration(
                VertexElement.Position<Vector3>(), VertexElement.Normal<Vector3>(), VertexElement.TextureCoordinate<Vector2>(0),
                VertexElement.TextureCoordinate<Vector2>(1));
        }

        /// <summary>Reads map.glb and adds its models as children of this entity; throws on a broken file.</summary>
        public void Build(MapPackage package, GraphicsDevice device)
        {
            MapModel model = MapGlbReader.Read(package.ModelPath);
            var textures = new Texture[model.Textures.Count];
            for (int i = 0; i < textures.Length; i++)
                owned.Add(textures[i] = MapTextures.Create(device, model.Textures[i], srgb: true));
            Texture white = MapTextures.Plain(device, 255, 255, 255, 255);
            owned.Add(white);

            var groups = new SortedDictionary<(int, MapShadows, int), List<MapMesh>>();
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                MapMesh m = model.Meshes[i];
                if (m.Collide == MapCollide.Only || m.Indices.Length == 0)
                    continue;
                // A baked mesh without lightmap UVs cannot be baked: it is drawn live.
                MapShadows shadows = m.Shadows == MapShadows.Baked && m.Uv1 == null ? MapShadows.Live : m.Shadows;
                // A live caster on its own: its shadow tile then is as small as the object.
                var key = (m.Material, shadows, shadows == MapShadows.Live ? i : -1);
                if (!groups.TryGetValue(key, out List<MapMesh> list))
                    groups[key] = list = new List<MapMesh>();
                list.Add(m);
            }

            var bounds = BoundingBox.Empty;
            int triangles = 0;
            foreach (var ((material, shadows, _), meshes) in groups)
            {
                Mesh mesh = shadows == MapShadows.Baked ? Merge<LitVertex>(meshes, true, device) : Merge<Vertex>(meshes, false, device);
                triangles += mesh.Draw.DrawCount / 3;
                bounds = BoundingBox.Merge(bounds, mesh.BoundingBox);
                Material look = Look(model.Materials[material], textures, white);
                var built = new Model { mesh };
                built.Materials.Add(new MaterialInstance(look) { IsShadowCaster = shadows != MapShadows.Off });
                built.BoundingBox = mesh.BoundingBox;
                var component = new ModelComponent(built) { RenderGroup = Group, IsShadowCaster = shadows != MapShadows.Off };
                Entity.AddChild(new Entity($"{model.Materials[material].Name} ({shadows})") { component });
            }
            Bounds = bounds;
            Counts = (model.Meshes.Count, groups.Count, triangles);
        }

        /// <summary>The meshes of one group in one vertex and index buffer, turned to Stride's winding
        /// (clockwise in front), with flat normals where a mesh brought none.</summary>
        Mesh Merge<T>(List<MapMesh> meshes, bool lit, GraphicsDevice device) where T : unmanaged
        {
            int vertexCount = 0, indexCount = 0;
            foreach (MapMesh m in meshes)
            {
                vertexCount += m.VertexCount;
                indexCount += m.Indices.Length;
            }
            var vertices = new T[vertexCount];
            var indices = new int[indexCount];
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            int v0 = 0, i0 = 0;
            foreach (MapMesh m in meshes)
            {
                float[] normals = m.Normals ?? FlatNormals(m);
                for (int v = 0; v < m.VertexCount; v++)
                {
                    var p = new Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]);
                    var n = new Vector3(normals[3 * v], normals[3 * v + 1], normals[3 * v + 2]);
                    var uv = m.Uv0 != null ? new Vector2(m.Uv0[2 * v], m.Uv0[2 * v + 1]) : Vector2.Zero;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    if (lit)
                    {
                        var lu = new Vector2(m.Uv1[2 * v], m.Uv1[2 * v + 1]);
                        var vertex = new LitVertex { Position = p, Normal = n, Uv = uv, LightUv = lu };
                        vertices[v0 + v] = MemoryMarshal.Cast<LitVertex, T>(MemoryMarshal.CreateSpan(ref vertex, 1))[0];
                    }
                    else
                    {
                        var vertex = new Vertex { Position = p, Normal = n, Uv = uv };
                        vertices[v0 + v] = MemoryMarshal.Cast<Vertex, T>(MemoryMarshal.CreateSpan(ref vertex, 1))[0];
                    }
                }
                for (int t = 0; t + 2 < m.Indices.Length; t += 3)
                {
                    indices[i0 + t] = v0 + m.Indices[t];
                    indices[i0 + t + 1] = v0 + m.Indices[t + 2];
                    indices[i0 + t + 2] = v0 + m.Indices[t + 1];
                }
                v0 += m.VertexCount;
                i0 += m.Indices.Length;
            }
            var box = new BoundingBox(min, max);
            Buffer vb = Buffer.Vertex.New(device, vertices, GraphicsResourceUsage.Immutable);
            Buffer ib = Buffer.Index.New(device, indices);
            owned.Add(vb);
            owned.Add(ib);
            return new Mesh
            {
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = indices.Length,
                    IndexBuffer = new IndexBufferBinding(ib, true, indices.Length),
                    VertexBuffers = new[] { new VertexBufferBinding(vb, lit ? LitVertex.Layout : Vertex.Layout, vertexCount) },
                },
                BoundingBox = box,
            };
        }

        static float[] FlatNormals(MapMesh m)
        {
            var n = new float[m.Positions.Length];
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                int a = m.Indices[t], b = m.Indices[t + 1], c = m.Indices[t + 2];
                var pa = new Vector3(m.Positions[3 * a], m.Positions[3 * a + 1], m.Positions[3 * a + 2]);
                var pb = new Vector3(m.Positions[3 * b], m.Positions[3 * b + 1], m.Positions[3 * b + 2]);
                var pc = new Vector3(m.Positions[3 * c], m.Positions[3 * c + 1], m.Positions[3 * c + 2]);
                Vector3 face = Vector3.Cross(pb - pa, pc - pa);
                foreach (int i in new[] { a, b, c })
                {
                    n[3 * i] += face.X;
                    n[3 * i + 1] += face.Y;
                    n[3 * i + 2] += face.Z;
                }
            }
            for (int i = 0; i < n.Length; i += 3)
            {
                var v = Vector3.Normalize(new Vector3(n[i], n[i + 1], n[i + 2]));
                n[i] = v.X;
                n[i + 1] = v.Y;
                n[i + 2] = v.Z;
            }
            return n;
        }

        /// <summary>A copy of the material's template with its values: same shader, so no new permutation.</summary>
        Material Look(MapMaterial m, Texture[] textures, Texture white)
        {
            Material template = m.Template switch { "Leaves" => Leaves, "Water" => Water, _ => Surface } ?? Surface;
            var look = new Material();
            foreach (MaterialPass pass in template.Passes)
            {
                var copy = new MaterialPass(new ParameterCollection(pass.Parameters))
                {
                    HasTransparency = pass.HasTransparency,
                    BlendState = pass.BlendState,
                    CullMode = pass.CullMode,
                    IsLightDependent = pass.IsLightDependent,
                    TessellationMethod = pass.TessellationMethod,
                    AlphaToCoverage = pass.AlphaToCoverage,
                    PassIndex = pass.PassIndex,
                };
                ParameterCollection p = copy.Parameters;
                p.Set(TintKey, new Vector4(m.Color[0], m.Color[1], m.Color[2], m.Color[3]));
                p.Set(TriplanarKey, m.Template == "Terrain" ? 1f : 0f);
                p.Set(ScaleKey, m.TextureScale);
                p.Set(ColorMapKey, m.ColorTexture >= 0 && m.ColorTexture < textures.Length ? textures[m.ColorTexture] : white);
                p.Set(MaterialKeys.GlossinessValue, Math.Clamp(1f - m.Roughness, 0f, 1f));
                p.Set(MaterialKeys.MetalnessValue, Math.Clamp(m.Metalness, 0f, 1f));
                look.Passes.Add(copy);
            }
            return look;
        }

        /// <summary>Frees the buffers and textures with the map (GameRoot removes the entity).</summary>
        public override void Cancel()
        {
            foreach (GraphicsResource resource in owned)
                resource.Dispose();
            owned.Clear();
        }
    }
}
