using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Small shells here and there on the beach (Luca), real geometry: scallops,
    /// cockles and spiral snails (ShellShapes) laid on the sand of the grass map
    /// (grassless ground at the beach's height), alone or two or three together.
    /// Built once into meshes of 16 m squares, drawn only near the camera. They
    /// take the light of the sand under them from the lightmap (ShellRenderFeature).
    /// Under each shell the sand is darker: a map of their places (SpotsKey) for
    /// the ground's TerrainTriplanar, laid once like the shells.
    /// Only looks: no collision, nothing of the simulation.
    /// </summary>
    /// <userdoc>Muscheln am Strand (nur Darstellung): Jakobsmuscheln, Herzmuscheln und kleine Schnecken.</userdoc>
    [Display("Muscheln")]
    public sealed class ShellField : SyncScript
    {
        /// <summary>The shells' render group: not voxelised, not in the lightmap bake.</summary>
        public const RenderGroup Group = RenderGroup.Group6;
        const float Square = 16f;

        /// <userdoc>Material der Muscheln (Farbe aus den Ecken).</userdoc>
        [DataMember(10)] public Material Material { get; set; }
        /// <userdoc>Graskarte der Map (eingebettete Ressource, von Tools/maps/MapUV): Sand ist Boden ohne Gras.</userdoc>
        [DataMember(11)] public string MapResource { get; set; } = "Maps/NeuGrass.bin";
        /// <userdoc>Sand liegt zwischen diesen Höhen in Metern (x bis y).</userdoc>
        [DataMember(20)] public Vector2 SandHeight { get; set; } = new(0.6f, 1.3f);
        /// <userdoc>Im Mittel so viele Quadratmeter Sand je Fundstelle.</userdoc>
        [DataMember(21)] public float AreaPerShell { get; set; } = 3.5f;
        /// <userdoc>Anteil der Fundstellen mit zwei oder drei Muscheln beieinander (0 bis 1).</userdoc>
        [DataMember(22)] public float GroupShare { get; set; } = 0.25f;
        /// <userdoc>Kleinste und größte Muschel in Metern (quer über die breiteste Stelle).</userdoc>
        [DataMember(23)] public Vector2 Size { get; set; } = new(0.075f, 0.175f);
        /// <userdoc>Anteile von Jakobsmuschel, Herzmuschel und Schnecke (werden auf 1 gerechnet).</userdoc>
        [DataMember(24)] public Vector3 KindShares { get; set; } = new(0.4f, 0.4f, 0.2f);
        /// <userdoc>Bis zu so vielen Metern von der Kamera sind die Muscheln zu sehen.</userdoc>
        [DataMember(30)] public float Reach { get; set; } = 35f;
        /// <userdoc>Startwert des Zufalls, der die Muscheln verteilt.</userdoc>
        [DataMember(31)] public int Seed { get; set; } = 1027;

        [StructLayout(LayoutKind.Sequential)]
        struct Vertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Color Color;
            // The lightmap texel of the sand underneath (ShellLight hands it on as TexCoord1).
            public Vector2 LightUV;

            public static readonly VertexDeclaration Layout = new VertexDeclaration(
                VertexElement.Position<Vector3>(), VertexElement.Normal<Vector3>(), VertexElement.Color<Color>(),
                VertexElement.TextureCoordinate<Vector2>(2));
        }

        // The shells' spots for TerrainTriplanar (its ShellSpots, same constants): cells of
        // SpotCell metres over the grass map, up to SpotSlots shells each (texels side by side),
        // a shell in every cell its spot reaches: (x, z) from the cell's corner - 0.5 m over
        // 2 m, radius over SpotRadius, alpha 1.
        static readonly ObjectParameterKey<Texture> SpotsKey = ParameterKeys.NewObject<Texture>(null, "TerrainTriplanar.ShellSpots");
        const float SpotCell = 0.5f, SpotRadius = 0.25f, SpotReach = 2.4f;   // reach: 3 ShellSigma
        const int SpotSlots = 4;
        readonly List<(Vector2 At, float Radius)> placed = new List<(Vector2, float)>();

        // The grass map: per cell lightmap uv, height (none: NaN), grass, shade.
        int size;
        float half, cell;
        float[] heights, shades;
        bool[] grassy;
        Vector2[] lightUV;

        readonly List<(ModelComponent Model, BoundingBox Box)> squares = new List<(ModelComponent, BoundingBox)>();
        CameraComponent camera;

        public override void Start()
        {
            if (Material == null || !LoadMap())
                return;
            ShellRenderFeature.Install(SceneSystem, Group);
            var built = new Dictionary<(int, int), (List<Vertex> Vertices, List<int> Indices)>();
            int count = Scatter(built);
            foreach (var (vertices, indices) in built.Values)
                AddSquare(vertices, indices);
            LaySpots();
            Console.WriteLine($"[muscheln] {count} in {squares.Count} Feldern");
        }

        /// <summary>Reads the grass map: what lies where, from above.</summary>
        bool LoadMap()
        {
            using Stream stream = typeof(ShellField).Assembly.GetManifestResourceStream(MapResource);
            if (stream == null)
                return false;
            using var reader = new BinaryReader(new ZLibStream(stream, CompressionMode.Decompress));
            if (reader.ReadUInt32() != 0x5352474B || reader.ReadUInt32() != 1)   // "KGRS", version 1
                throw new InvalidDataException($"{MapResource} hat ein unbekanntes Format.");
            size = reader.ReadInt32();
            half = reader.ReadSingle();
            float maxHeight = reader.ReadSingle();
            cell = 2f * half / size;
            heights = new float[size * size];
            shades = new float[size * size];
            grassy = new bool[size * size];
            lightUV = new Vector2[size * size];
            for (int i = 0; i < size * size; i++)
            {
                float u = reader.ReadUInt16() / 65535f, v = reader.ReadUInt16() / 65535f;
                ushort height = reader.ReadUInt16(), packed = reader.ReadUInt16();
                lightUV[i] = new Vector2(u, v);
                heights[i] = height == 0 ? float.NaN : height / 65535f * maxHeight;
                grassy[i] = packed >> 8 != 0;
                shades[i] = (packed & 255) / 255f;
            }
            return true;
        }

        // Sand: ground without grass at the beach's height.
        bool Sand(int x, int z)
        {
            if (x < 0 || z < 0 || x >= size || z >= size)
                return false;
            int i = z * size + x;
            return !grassy[i] && heights[i] >= SandHeight.X && heights[i] <= SandHeight.Y;
        }

        /// <summary>
        /// Every sand cell with sand all round may hold a find, by chance after its
        /// area; a find is one shell, or a small group in a hand's breadth.
        /// </summary>
        int Scatter(Dictionary<(int, int), (List<Vertex>, List<int>)> built)
        {
            var random = new Random(Seed);
            float chance = cell * cell / Math.Max(AreaPerShell, 0.01f);
            int count = 0;
            for (int z = 1; z < size - 1; z++)
                for (int x = 1; x < size - 1; x++)
                {
                    if (random.NextDouble() >= chance || !SandAround(x, z))
                        continue;
                    var at = new Vector2((x + (float)random.NextDouble()) * cell - half, (z + (float)random.NextDouble()) * cell - half);
                    int shells = random.NextDouble() < GroupShare ? 2 + random.Next(2) : 1;
                    for (int k = 0; k < shells; k++)
                    {
                        Vector2 place = at;
                        if (k > 0)
                        {
                            float angle = (float)random.NextDouble() * MathUtil.TwoPi, away = 0.06f + 0.2f * (float)random.NextDouble();
                            place += new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * away;
                        }
                        if (Place(place, random, built))
                            count++;
                    }
                }
            return count;
        }

        bool SandAround(int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!Sand(x + dx, z + dz))
                        return false;
            return true;
        }

        /// <summary>One shell at p on the sand: turned at random, tilted with the ground, a little sunk in.</summary>
        bool Place(Vector2 p, Random random, Dictionary<(int, int), (List<Vertex> Vertices, List<int> Indices)> built)
        {
            float fx = (p.X + half) / cell - 0.5f, fz = (p.Y + half) / cell - 0.5f;
            int x = (int)MathF.Floor(fx), z = (int)MathF.Floor(fz);
            if (!SandAround(x, z) || !SandAround(x + 1, z + 1))
                return false;
            float tx = fx - x, tz = fz - z;
            float H(int cx, int cz) => heights[cz * size + cx];
            float y = MathUtil.Lerp(MathUtil.Lerp(H(x, z), H(x + 1, z), tx), MathUtil.Lerp(H(x, z + 1), H(x + 1, z + 1), tx), tz);
            int nx = tx < 0.5f ? x : x + 1, nz = tz < 0.5f ? z : z + 1;
            var up = Vector3.Normalize(new Vector3(-(H(nx + 1, nz) - H(nx - 1, nz)) / (2f * cell), 1f, -(H(nx, nz + 1) - H(nx, nz - 1)) / (2f * cell)));
            int nearest = nz * size + nx;

            Vector3 shares = KindShares;
            float pick = (float)random.NextDouble() * (shares.X + shares.Y + shares.Z);
            int kind = pick < shares.X ? 0 : pick < shares.X + shares.Y ? 1 : 2;
            ShellShapes.Piece piece = ShellShapes.Make(kind, random);
            float scale = MathUtil.Lerp(Size.X, Size.Y, (float)random.NextDouble());
            float yaw = (float)random.NextDouble() * MathUtil.TwoPi;
            float tiltX = ((float)random.NextDouble() - 0.5f) * 0.25f, tiltZ = ((float)random.NextDouble() - 0.5f) * 0.25f;
            float sink = Math.Max(0.002f, 0.1f * Height(piece) * scale);

            Quaternion toGround = Quaternion.BetweenDirections(Vector3.UnitY, up);
            Matrix turn = Matrix.RotationYawPitchRoll(yaw, tiltX, tiltZ) * Matrix.RotationQuaternion(toGround);
            Matrix world = Matrix.Scaling(scale) * Matrix.Translation(0f, -sink, 0f) * turn * Matrix.Translation(p.X, y, p.Y);
            float shade = shades[nearest];

            var key = ((int)MathF.Floor(p.X / Square), (int)MathF.Floor(p.Y / Square));
            if (!built.TryGetValue(key, out var square))
                built[key] = square = (new List<Vertex>(), new List<int>());
            int first = square.Vertices.Count;
            for (int i = 0; i < piece.Positions.Count; i++)
            {
                Color4 c = piece.Colors[i];
                square.Vertices.Add(new Vertex
                {
                    Position = Vector3.TransformCoordinate(piece.Positions[i], world),
                    Normal = Vector3.Normalize(Vector3.TransformNormal(piece.Normals[i], turn)),
                    Color = (Color)new Color4(c.R * shade, c.G * shade, c.B * shade, 1f),
                    LightUV = lightUV[nearest],
                });
            }
            foreach (int index in piece.Indices)
                square.Indices.Add(first + index);
            placed.Add((p, 0.5f * scale));
            return true;
        }

        /// <summary>
        /// The spot map from the placed shells, handed to the map's models; a cell
        /// with more shells than slots keeps the first ones (logged).
        /// </summary>
        void LaySpots()
        {
            int cells = (int)MathF.Round(2f * half / SpotCell), dropped = 0;
            var texels = new byte[cells * SpotSlots * cells * 4];
            foreach (var (at, radius) in placed)
            {
                float reach = SpotReach * radius;
                int x0 = (int)MathF.Floor((at.X - reach + half) / SpotCell), x1 = (int)MathF.Floor((at.X + reach + half) / SpotCell);
                int z0 = (int)MathF.Floor((at.Y - reach + half) / SpotCell), z1 = (int)MathF.Floor((at.Y + reach + half) / SpotCell);
                for (int z = Math.Max(0, z0); z <= Math.Min(cells - 1, z1); z++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(cells - 1, x1); x++)
                    {
                        int slot = 0, row = (z * cells + x) * SpotSlots * 4;
                        while (slot < SpotSlots && texels[row + 4 * slot + 3] != 0)
                            slot++;
                        if (slot == SpotSlots)
                        {
                            dropped++;
                            continue;
                        }
                        int t = row + 4 * slot;
                        texels[t] = Unorm8((at.X - (x * SpotCell - half) + 0.5f) / 2f);
                        texels[t + 1] = Unorm8((at.Y - (z * SpotCell - half) + 0.5f) / 2f);
                        texels[t + 2] = Unorm8(radius / SpotRadius);
                        texels[t + 3] = 255;
                    }
            }
            Texture spots = Texture.New2D(GraphicsDevice, cells * SpotSlots, cells, PixelFormat.R8G8B8A8_UNorm, texels);
            foreach (Entity root in Entity.Scene.Entities)
                Hand(root, spots);
            if (dropped > 0)
                Console.WriteLine($"[muscheln] {dropped} Schattenflecken ohne Platz");
        }

        // The models of the map's scene (the ground among them) get the spot map; materials without
        // ShellSpots ignore it. The model loads its own material, not one named here.
        static void Hand(Entity entity, Texture spots)
        {
            if (entity.Get<ModelComponent>() is { Model: { } model })
                foreach (MaterialInstance m in model.Materials)
                    if (m.Material != null)
                        foreach (MaterialPass pass in m.Material.Passes)
                            pass.Parameters.Set(SpotsKey, spots);
            foreach (TransformComponent child in entity.Transform.Children)
                Hand(child.Entity, spots);
        }

        static byte Unorm8(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);

        static float Height(ShellShapes.Piece piece)
        {
            float top = 0f;
            foreach (Vector3 p in piece.Positions)
                top = Math.Max(top, p.Y);
            return top;
        }

        /// <summary>One 16 m square's shells as a mesh on its own entity.</summary>
        void AddSquare(List<Vertex> vertices, List<int> indices)
        {
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            foreach (Vertex v in vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            var box = new BoundingBox(min, max);
            Buffer vb = Buffer.Vertex.New(GraphicsDevice, vertices.ToArray(), GraphicsResourceUsage.Immutable);
            Buffer ib = Buffer.Index.New(GraphicsDevice, indices.ToArray());
            var mesh = new Mesh
            {
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = indices.Count,
                    IndexBuffer = new IndexBufferBinding(ib, true, indices.Count),
                    VertexBuffers = new[] { new VertexBufferBinding(vb, Vertex.Layout, vertices.Count) },
                },
                BoundingBox = box,
            };
            var model = new Model { mesh };
            model.Materials.Add(new MaterialInstance(Material) { IsShadowCaster = false });
            model.BoundingBox = box;
            var component = new ModelComponent(model) { RenderGroup = Group, IsShadowCaster = false, Enabled = false };
            var entity = new Entity("Shells") { component };
            Entity.AddChild(entity);
            squares.Add((component, box));
        }

        public override void Update()
        {
            if (squares.Count == 0 || (camera ??= FindCamera()) == null)
                return;
            Vector3 eye = camera.Entity.Transform.WorldMatrix.TranslationVector;
            foreach (var (model, box) in squares)
            {
                Vector3 nearest = Vector3.Clamp(eye, box.Minimum, box.Maximum);
                model.Enabled = Vector3.DistanceSquared(nearest, eye) < Reach * Reach;
            }
        }

        CameraComponent FindCamera()
        {
            if (SceneSystem.SceneInstance?.RootScene is not { } scene)
                return null;
            foreach (Entity e in scene.Entities)
                if (e.Get<CameraComponent>() is { } found)
                    return found;
            return null;
        }
    }
}
