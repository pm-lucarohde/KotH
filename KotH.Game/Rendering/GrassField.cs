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
    /// drawn instanced on the map's grassy 2 m patches near the camera and in
    /// view, one draw; GrassBlade builds the blades on the ground of the grass
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
        [DataMember(20)] public float FullReach { get; set; } = 15f;
        /// <userdoc>Ab so vielen Metern sind die Halme ganz weg (dazwischen dünner), dort bleibt nur die Bodentextur.</userdoc>
        [DataMember(21)] public float FarReach { get; set; } = 40f;
        /// <userdoc>Höhe der kürzesten Halme in Metern.</userdoc>
        [DataMember(22)] public float HeightMin { get; set; } = 0.25f;
        /// <userdoc>Höhe der längsten Halme in Metern.</userdoc>
        [DataMember(23)] public float HeightMax { get; set; } = 0.45f;
        /// <userdoc>Breite eines Halms am Fuß in Metern.</userdoc>
        [DataMember(24)] public float Width { get; set; } = 0.06f;
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
        Matrix[] shown = Array.Empty<Matrix>();
        int shownCount = -1;
        readonly List<Matrix> wanted = new List<Matrix>();
        readonly Vector4[] figures = new Vector4[MaxFigures];
        InstancingUserArray instancing;
        CameraComponent camera;
        PlayerVisualSystem visuals;

        public override void Start()
        {
            if (Material == null || !LoadMap())
                return;
            var model = new Model { GrassPatch.Build(GraphicsDevice) };
            model.Materials.Add(new MaterialInstance(Material) { IsShadowCaster = false });
            model.BoundingBox = model.Meshes[0].BoundingBox;
            instancing = new InstancingUserArray();
            Entity.Add(new ModelComponent(model) { RenderGroup = Group, IsShadowCaster = false });
            Entity.Add(new InstancingComponent { Type = instancing });
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
            if (instancing == null || (camera ??= FindCamera()) == null)
                return;
            ParameterCollection parameters = Material.Passes[0].Parameters;
            parameters.Set(ReachKey, new Vector4(FullReach, FarReach, HeightMin, HeightMax));
            parameters.Set(FormKey, new Vector4(Width, 0f, PetalRadius, FlowerShare));
            Vector2 axis = RenderContext.GetShared(Services).Tags.Get(Atmosphere.Key)?.WindAxis ?? Vector2.UnitX;
            parameters.Set(WindKey, new Vector4(axis.X, axis.Y, GustSpeed, GustLength));
            parameters.Set(BendKey, new Vector4(StillLean, GustLean, Flutter, FigurePush));
            Figures();
            parameters.Set(FiguresKey, MaxFigures, ref figures[0]);
            Patches();
        }

        /// <summary>The patches within reach and in view (the camera's last matrices, a patch wider).</summary>
        void Patches()
        {
            Vector3 eye = camera.Entity.Transform.WorldMatrix.TranslationVector;
            Matrix viewProjection = camera.ViewProjectionMatrix;
            var frustum = new BoundingFrustum(ref viewProjection);
            float reach = FarReach + GrassPatch.Size;
            wanted.Clear();
            foreach (Vector2 p in patches)
            {
                float dx = p.X + 0.5f * GrassPatch.Size - eye.X, dz = p.Y + 0.5f * GrassPatch.Size - eye.Z;
                if (dx * dx + dz * dz > reach * reach)
                    continue;
                var box = new BoundingBoxExt(new BoundingBox(new Vector3(p.X - 1f, 0f, p.Y - 1f),
                    new Vector3(p.X + GrassPatch.Size + 1f, maxHeight, p.Y + GrassPatch.Size + 1f)));
                if (frustum.Contains(ref box))
                    wanted.Add(Matrix.Translation(p.X, 0f, p.Y));
            }
            if (wanted.Count == shownCount && Same())
                return;
            if (shown.Length < wanted.Count)
                shown = new Matrix[Math.Max(wanted.Count, 2 * shown.Length)];
            wanted.CopyTo(shown);
            shownCount = wanted.Count;
            instancing.UpdateWorldMatrices(shown, shownCount);
        }

        bool Same()
        {
            for (int i = 0; i < wanted.Count; i++)
                if (wanted[i].M41 != shown[i].M41 || wanted[i].M43 != shown[i].M43)
                    return false;
            return true;
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
            map?.Dispose();
            map = null;
        }
    }
}
