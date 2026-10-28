using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using KotH.Client.Player;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Grass blades on the meadow, after Ghost of Tsushima (Luca): GrassPatch
    /// drawn instanced on the map's grassy 2 m patches in reach and in view,
    /// two draws (far patches with a quarter of the blades); GrassBlade builds the blades on the ground of the grass
    /// map (Tools/maps/MapUV writes it), GrassColor paints them. Only looks:
    /// no collision, nothing of the simulation. Hands the shader the wind of
    /// the Atmosphere and the figures' feet every frame.
    /// </summary>
    /// <userdoc>Grashalme auf der Wiese (nur Darstellung), mit Wind, Blumen und Figuren, die sie wegdrücken.</userdoc>
    [Display("Gras")]
    public sealed class GrassField : SyncScript
    {
        /// <summary>The grass's render group: not voxelised, not in the lightmap bake.</summary>
        public const RenderGroup Group = RenderGroup.Group5;

        static readonly ObjectParameterKey<Texture> MapKey = ParameterKeys.NewObject<Texture>(null, "GrassBlade.GrassMap");
        static readonly ValueParameterKey<Vector4> MapShapeKey = ParameterKeys.NewValue<Vector4>(default, "GrassBlade.GrassMapShape");
        static readonly ValueParameterKey<Vector4> ReachKey = ParameterKeys.NewValue<Vector4>(default, "GrassBlade.GrassReach");
        static readonly ValueParameterKey<Vector4> FormKey = ParameterKeys.NewValue<Vector4>(default, "GrassBlade.GrassForm");
        static readonly ValueParameterKey<Vector4> WindKey = ParameterKeys.NewValue<Vector4>(default, "GrassBlade.GrassWind");
        static readonly ValueParameterKey<Vector4> BendKey = ParameterKeys.NewValue<Vector4>(default, "GrassBlade.GrassBend");
        static readonly ValueParameterKey<Vector4> FiguresKey = ParameterKeys.NewValue<Vector4>(default, "GrassBlade.GrassFigures");
        const int MaxFigures = 12;
        // The figure's feet below its centre (Mushroom, 1.8 m).
        const float FeetBelow = 0.9f;

        /// <userdoc>Material der Halme (GrassColor).</userdoc>
        [DataMember(10)] public Material Material { get; set; }
        /// <userdoc>Graskarte der Map (eingebettete Ressource, von Tools/maps/MapUV).</userdoc>
        [DataMember(11)] public string MapResource { get; set; } = "Maps/NeuGrass.bin";
        /// <userdoc>Bis zu so vielen Metern von der Kamera stehen die Halme voll dicht.</userdoc>
        [DataMember(20)] public float FullReach { get; set; } = 25f;
        /// <userdoc>Ab so vielen Metern sind die Halme ganz weg (dazwischen dünner), dort bleibt nur die Bodentextur.</userdoc>
        [DataMember(21)] public float FarReach { get; set; } = 90f;
        /// <userdoc>Höhe der kürzesten Halme in Metern.</userdoc>
        [DataMember(22)] public float HeightMin { get; set; } = 0.125f;
        /// <userdoc>Höhe der längsten Halme in Metern.</userdoc>
        [DataMember(23)] public float HeightMax { get; set; } = 0.225f;
        /// <userdoc>Breite eines Halms am Fuß in Metern.</userdoc>
        [DataMember(24)] public float Width { get; set; } = 0.03f;
        /// <userdoc>Radius einer Blüte in Metern.</userdoc>
        [DataMember(25)] public float PetalRadius { get; set; } = 0.08f;
        /// <userdoc>Anteil der 4-m-Flecken mit Blumen (0 bis 1).</userdoc>
        [DataMember(26)] public float FlowerShare { get; set; } = 0.3f;
        /// <userdoc>Tempo der Böenwellen über das Feld in m/s.</userdoc>
        [DataMember(30)] public float GustSpeed { get; set; } = 3.5f;
        /// <userdoc>Abstand der Böenwellen in Metern.</userdoc>
        [DataMember(31)] public float GustLength { get; set; } = 14f;
        /// <userdoc>Eigene Neigung der Halme (Anteil der Höhe).</userdoc>
        [DataMember(32)] public float StillLean { get; set; } = 0.12f;
        /// <userdoc>Neigung in einer Böe (Anteil der Höhe).</userdoc>
        [DataMember(33)] public float GustLean { get; set; } = 0.45f;
        /// <userdoc>Flattern der einzelnen Halme (Anteil der Höhe).</userdoc>
        [DataMember(34)] public float Flutter { get; set; } = 0.05f;
        /// <userdoc>Wie stark Figuren die Halme wegdrücken (Anteil der Höhe).</userdoc>
        [DataMember(35)] public float FigurePush { get; set; } = 2f;
        /// <userdoc>Bis zu so vielen Metern um eine Figur biegen sich die Halme weg.</userdoc>
        [DataMember(36)] public float FigureReach { get; set; } = 0.7f;

        Texture map;
        float half, cell, maxHeight;
        readonly List<Vector2> patches = new List<Vector2>();
        readonly Vector4[] figures = new Vector4[MaxFigures];
        // Near patches carry every blade, far ones (density there at most
        // FarShare) only the low numbers GrassBlade keeps there anyway.
        const float FarShare = 0.25f;
        Patches near, far;
        CameraComponent camera;
        PlayerVisualSystem visuals;

        public override void Start()
        {
            if (Material == null || !LoadMap())
                return;
            near = new Patches(this, Entity, GrassPatch.Blades);
            var farEntity = new Entity("GrassFar");
            Entity.AddChild(farEntity);
            far = new Patches(this, farEntity, (int)(GrassPatch.Blades * FarShare));
            GrassRenderFeature.Install(SceneSystem, Group);
            Material.Passes[0].Parameters.Set(MapKey, map);
            Material.Passes[0].Parameters.Set(MapShapeKey, new Vector4(1f / cell, half, map.Width, maxHeight));
        }

        /// <summary>The grass map as a texture, and the 2 m patches that have grass.</summary>
        bool LoadMap()
        {
            using Stream stream = typeof(GrassField).Assembly.GetManifestResourceStream(MapResource);
            if (stream == null)
                return false;
            using var reader = new BinaryReader(new ZLibStream(stream, CompressionMode.Decompress));
            if (reader.ReadUInt32() != 0x5352474B || reader.ReadUInt32() != 1)   // "KGRS", version 1
                throw new InvalidDataException($"{MapResource} hat ein unbekanntes Format.");
            int size = reader.ReadInt32();
            half = reader.ReadSingle();
            maxHeight = reader.ReadSingle();
            cell = 2f * half / size;
            var texels = new ushort[size * size * 4];
            for (int i = 0; i < texels.Length; i++)
                texels[i] = reader.ReadUInt16();
            map = Texture.New2D(GraphicsDevice, size, size, PixelFormat.R16G16B16A16_UNorm, texels);

            int per = (int)MathF.Round(GrassPatch.Size / cell);
            for (int pz = 0; pz < size / per; pz++)
                for (int px = 0; px < size / per; px++)
                {
                    bool grass = false;
                    for (int z = pz * per; z < (pz + 1) * per && !grass; z++)
                        for (int x = px * per; x < (px + 1) * per && !grass; x++)
                            grass = texels[4 * (z * size + x) + 3] >> 8 != 0;
                    if (grass)
                        patches.Add(new Vector2(px * GrassPatch.Size - half, pz * GrassPatch.Size - half));
                }
            return true;
        }

        public override void Update()
        {
            if (near == null || (camera ??= FindCamera()) == null)
                return;
            ParameterCollection parameters = Material.Passes[0].Parameters;
            parameters.Set(ReachKey, new Vector4(FullReach, FarReach, HeightMin, HeightMax));
            parameters.Set(FormKey, new Vector4(Width, 0f, PetalRadius, FlowerShare));
            Vector2 axis = RenderContext.GetShared(Services).Tags.Get(Atmosphere.Key)?.WindAxis ?? Vector2.UnitX;
            parameters.Set(WindKey, new Vector4(axis.X, axis.Y, GustSpeed, GustLength));
            parameters.Set(BendKey, new Vector4(StillLean, GustLean, Flutter, FigurePush));
            Figures();
            parameters.Set(FiguresKey, MaxFigures, ref figures[0]);
            Choose();
        }

        /// <summary>
        /// The patches within reach and in view (the camera's last matrices, a
        /// patch wider), near or far by their nearest point's density.
        /// </summary>
        void Choose()
        {
            Vector3 eye = camera.Entity.Transform.WorldMatrix.TranslationVector;
            Matrix viewProjection = camera.ViewProjectionMatrix;
            var frustum = new BoundingFrustum(ref viewProjection);
            near.Wanted.Clear();
            far.Wanted.Clear();
            foreach (Vector2 p in patches)
            {
                float dx = Math.Max(0f, Math.Max(p.X - eye.X, eye.X - p.X - GrassPatch.Size));
                float dz = Math.Max(0f, Math.Max(p.Y - eye.Z, eye.Z - p.Y - GrassPatch.Size));
                float distance = MathF.Sqrt(dx * dx + dz * dz);
                if (distance > FarReach)
                    continue;
                var box = new BoundingBoxExt(new BoundingBox(new Vector3(p.X - 1f, 0f, p.Y - 1f),
                    new Vector3(p.X + GrassPatch.Size + 1f, maxHeight, p.Y + GrassPatch.Size + 1f)));
                if (!frustum.Contains(ref box))
                    continue;
                float t = Math.Clamp((distance - FullReach) / Math.Max(FarReach - FullReach, 0.01f), 0f, 1f);
                float density = 1f - t * t * (3f - 2f * t);
                (density <= FarShare ? far : near).Wanted.Add(Matrix.Translation(p.X, 0f, p.Y));
            }
            CommandList commandList = Game.GraphicsContext.CommandList;
            near.Show(commandList);
            far.Show(commandList);
        }

        /// <summary>
        /// One patch mesh drawn instanced on an entity, and where it stands now.
        /// Its own buffers, three pairs in turn: Stride's Vulkan writes a dynamic
        /// buffer (InstancingUserArray's) straight into the memory the frame before
        /// may still draw from, so its depth prepass and colour pass could see two
        /// lists, and a patch with depth but no colour flashed in the clear colour.
        /// </summary>
        sealed class Patches
        {
            // Stride keeps at most two frames in flight (two frame fences).
            const int Ring = 3;
            public readonly List<Matrix> Wanted = new List<Matrix>();
            readonly InstancingUserBuffer instancing = new InstancingUserBuffer();
            readonly Buffer<Matrix>[] worlds = new Buffer<Matrix>[Ring], inverses = new Buffer<Matrix>[Ring];
            readonly GraphicsDevice device;
            Matrix[] shown = Array.Empty<Matrix>(), shownInverse = Array.Empty<Matrix>();
            int shownCount = -1, slot;

            public Patches(GrassField field, Entity entity, int blades)
            {
                device = field.GraphicsDevice;
                var model = new Model { GrassPatch.Build(device, blades) };
                model.Materials.Add(new MaterialInstance(field.Material) { IsShadowCaster = false });
                model.BoundingBox = model.Meshes[0].BoundingBox;
                entity.Add(new ModelComponent(model) { RenderGroup = Group, IsShadowCaster = false });
                entity.Add(new InstancingComponent { Type = instancing });
            }

            /// <summary>Hands the wanted patches to the instancing, if they changed, in the next buffers.</summary>
            public void Show(CommandList commandList)
            {
                if (Wanted.Count == shownCount && Same())
                    return;
                int count = Wanted.Count;
                if (shown.Length < count)
                {
                    shown = new Matrix[Math.Max(count, 2 * shown.Length)];
                    shownInverse = new Matrix[shown.Length];
                }
                Wanted.CopyTo(shown);
                shownCount = count;
                BoundingBox box = BoundingBox.Empty;
                for (int i = 0; i < count; i++)
                {
                    Vector3 at = shown[i].TranslationVector;
                    shownInverse[i] = Matrix.Translation(-at);
                    BoundingBox.Merge(ref box, ref at, out box);
                }

                slot = (slot + 1) % Ring;
                if (count > 0 && (worlds[slot] == null || worlds[slot].ElementCount < count))
                {
                    worlds[slot]?.Dispose();
                    inverses[slot]?.Dispose();
                    worlds[slot] = Stride.Graphics.Buffer.New<Matrix>(device, shown.Length, BufferFlags.ShaderResource | BufferFlags.StructuredBuffer, GraphicsResourceUsage.Dynamic);
                    inverses[slot] = Stride.Graphics.Buffer.New<Matrix>(device, shown.Length, BufferFlags.ShaderResource | BufferFlags.StructuredBuffer, GraphicsResourceUsage.Dynamic);
                }
                if (count > 0)
                {
                    worlds[slot].SetData(commandList, (ReadOnlySpan<Matrix>)shown.AsSpan(0, count));
                    inverses[slot].SetData(commandList, (ReadOnlySpan<Matrix>)shownInverse.AsSpan(0, count));
                }
                instancing.InstanceWorldBuffer = worlds[slot];
                instancing.InstanceWorldInverseBuffer = inverses[slot];
                instancing.InstanceCount = count;
                instancing.BoundingBox = box;
            }

            public void Dispose()
            {
                for (int i = 0; i < Ring; i++)
                {
                    worlds[i]?.Dispose();
                    inverses[i]?.Dispose();
                }
            }

            bool Same()
            {
                for (int i = 0; i < Wanted.Count; i++)
                    if (Wanted[i].M41 != shown[i].M41 || Wanted[i].M43 != shown[i].M43)
                        return false;
                return true;
            }
        }

        /// <summary>The figures' feet, as shown (interpolated), for the blades to bend away.</summary>
        void Figures()
        {
            Array.Clear(figures);
            visuals ??= Find<PlayerVisualSystem>();
            if (visuals == null)
                return;
            int n = 0;
            foreach (Entity figure in visuals.Figures)
            {
                if (n == MaxFigures)
                    break;
                Vector3 at = figure.Transform.WorldMatrix.TranslationVector;
                figures[n++] = new Vector4(at.X, at.Y - FeetBelow * figure.Transform.Scale.Y, at.Z, FigureReach);
            }
        }

        CameraComponent FindCamera() => Find<CameraComponent>();

        T Find<T>() where T : EntityComponent
        {
            if (SceneSystem.SceneInstance?.RootScene is not { } scene)
                return null;
            foreach (Entity e in scene.Entities)
                if (e.Get<T>() is { } found)
                    return found;
            return null;
        }

        public override void Cancel()
        {
            near?.Dispose();
            far?.Dispose();
            map?.Dispose();
            map = null;
        }
    }
}
