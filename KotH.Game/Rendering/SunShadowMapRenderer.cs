using System;
using System.Collections.Generic;
using KotH.Client.Settings;
using Stride.Core;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Shaders;

namespace KotH.Client.Rendering
{
    /// <summary>One view's shadow tiles on the sheet, as <see cref="FigureShadowMasks"/> filters them. Stride's own
    /// single cascade in it stays empty: nothing reads it.</summary>
    public sealed class SunShadowTiles : LightDirectionalShadowMapRenderer.ShaderData
    {
        /// <summary>The most tiles a view keeps (the shader's arrays).</summary>
        public const int Most = 32;

        public int Count;
        /// <summary>World to atlas uv; depth 0 at the tile's sun side, 1 at its far side.</summary>
        public readonly Matrix[] WorldToUV = new Matrix[Most];
        /// <summary>Atlas uv: left, top, right, bottom.</summary>
        public readonly Vector4[] Rects = new Vector4[Most];
        /// <summary>The view's uv the tile's shadow can reach: left, top, right, bottom.</summary>
        public readonly Vector4[] Screens = new Vector4[Most];
        /// <summary>The tile's top pyramid level, the metres of its depth range, the metres per texel.</summary>
        public readonly Vector4[] Scales = new Vector4[Most];
        /// <summary>The sheet in the atlas and the part of it that holds tiles, in texels.</summary>
        public Rectangle Sheet;
        public Int2 Used;
        /// <summary>Metres a shadow is followed from its caster.</summary>
        public float Throw;

        public SunShadowTiles() : base(1) { }
    }

    /// <summary>Stride's directional shadow map renderer receiving with <c>CascadeShadowReceiver</c>. The map
    /// casts once into <see cref="StaticSunShadowRenderer"/>'s map; what moves (figures, ropes, a custom map's
    /// live objects) gets a tile of its own on one sheet instead of cascades, as fine as the screen shows
    /// it, and <see cref="FigureShadowMasks"/> filters the tiles per pixel. Clouds shade on top, live.</summary>
    [DataContract("SunShadowMapRenderer")]
    public sealed class SunShadowMapRenderer : LightDirectionalShadowMapRenderer
    {
        static readonly ValueParameterKey<Vector2> FigureShadowSize = ParameterKeys.NewValue<Vector2>(default, "CascadeShadowReceiver.FigureShadowSize");
        static readonly ObjectParameterKey<Texture> FigureShadow = ParameterKeys.NewObject<Texture>(null, "CascadeShadowReceiver.FigureShadow");
        static readonly ValueParameterKey<float> SunTangent = ParameterKeys.NewValue<float>(default, "CascadeShadowReceiver.SunTangent");
        static readonly ValueParameterKey<float> MaxPenumbraKey = ParameterKeys.NewValue<float>(default, "CascadeShadowReceiver.MaxPenumbra");
        static readonly ValueParameterKey<Matrix> WorldToStaticUV = ParameterKeys.NewValue<Matrix>(default, "CascadeShadowReceiver.WorldToStaticUV");
        static readonly ValueParameterKey<Vector2> StaticTexelSize = ParameterKeys.NewValue<Vector2>(default, "CascadeShadowReceiver.StaticTexelSize");
        static readonly ObjectParameterKey<Texture> StaticShadowMap = ParameterKeys.NewObject<Texture>(null, "CascadeShadowReceiver.StaticShadowMap");
        static readonly ObjectParameterKey<Texture> SunLightmap = ParameterKeys.NewObject<Texture>(null, "CascadeShadowReceiver.SunLightmap");
        static readonly ValueParameterKey<Vector2> SunLightmapTexelSize = ParameterKeys.NewValue<Vector2>(default, "CascadeShadowReceiver.SunLightmapTexelSize");
        static readonly ValueParameterKey<Vector4> CloudShadowLayer = ParameterKeys.NewValue<Vector4>(default, "CascadeShadowReceiver.CloudShadowLayer");
        static readonly ValueParameterKey<Vector4> CloudShadowScale = ParameterKeys.NewValue<Vector4>(default, "CascadeShadowReceiver.CloudShadowScale");
        static readonly ValueParameterKey<Vector4> CloudShadowOffset = ParameterKeys.NewValue<Vector4>(default, "CascadeShadowReceiver.CloudShadowOffset");
        static readonly ValueParameterKey<Vector2> CloudShadowWind = ParameterKeys.NewValue<Vector2>(default, "CascadeShadowReceiver.CloudShadowWind");
        static readonly ValueParameterKey<Vector3> CloudShadowToSun = ParameterKeys.NewValue<Vector3>(default, "CascadeShadowReceiver.CloudShadowToSun");
        static readonly ObjectParameterKey<Texture> CloudShadowMap = ParameterKeys.NewObject<Texture>(null, "CascadeShadowReceiver.CloudShadowMap");

        /// <summary>Angular radius of the sun in degrees: the penumbra per metre between blocker and receiver.</summary>
        /// <userdoc>Apparent radius of the sun in degrees: the larger, the wider the penumbra per metre from the caster.</userdoc>
        [DataMember(100)]
        public float SunAngle { get; set; } = 1.0f;

        /// <summary>Widest penumbra radius of the static map in its texels; its blockers are searched as far.</summary>
        /// <userdoc>Largest penumbra radius of the map's own shadow in texels of the static map; casters are searched this far too.</userdoc>
        [DataMember(110)]
        public float MaxPenumbra { get; set; } = 4.0f;

        /// <summary>Edge of the sheet the tiles share, in texels, times the Shadows setting's share.</summary>
        /// <userdoc>Edge length in texels of the sheet all moving casters' shadow tiles share (a power of two).</userdoc>
        [DataMember(120)]
        public int Sheet { get; set; } = 2048;

        /// <summary>The finest texel of a tile in metres (the nearest casters), times the Shadows setting's share.</summary>
        /// <userdoc>Finest texel of a caster's shadow tile in metres; farther casters get coarser ones, about one per pixel.</userdoc>
        [DataMember(130)]
        public float Texel { get; set; } = 0.003125f;

        /// <summary>Metres a shadow is followed from its caster; it fades out over the last quarter.</summary>
        /// <userdoc>How far from its caster a shadow is drawn, in metres; it fades out over the last quarter.</userdoc>
        [DataMember(140)]
        public float Throw { get; set; } = 40f;

        /// <summary>Casters farther than this from the camera get no tile.</summary>
        /// <userdoc>Distance from the camera in metres beyond which moving casters cast no shadow.</userdoc>
        [DataMember(150)]
        public float Reach { get; set; } = 150f;

        /// <summary>The most tiles (shadow views) a view gets: Stride spends about 0.1 ms of CPU on each.</summary>
        /// <userdoc>Most shadow tiles per camera; beyond that, casters near each other share a tile (each costs CPU time).</userdoc>
        [DataMember(160)]
        public int Tiles { get; set; } = 6;

        /// <summary>Draws the figures' shadow per pixel; set by <see cref="FigureShadowMasks.Install"/>.</summary>
        [DataMemberIgnore] public FigureShadowMasks Masks { get; set; }

        // Texels of the pyramid's coarsest block a tile's corner is held to, and the smallest tile.
        const int Cell = 32;
        const int MostUnits = 256, MostClusters = 96;
        static readonly bool LogTiles = Environment.GetEnvironmentVariable("KOTH_SHADOWLOG") != null;

        // The view each shadow map texture (pooled, reused every frame) was made for.
        readonly Dictionary<LightShadowMapTexture, RenderView> viewOf = new();
        PoolListStruct<SunShadowTiles> tilePool = new(4, () => new SunShadowTiles());

        // Casters by model, then joined into clusters: world boxes, the eye's distance, the screen reached.
        readonly object[] owners = new object[MostUnits];
        readonly Vector3[] unitMin = new Vector3[MostUnits], unitMax = new Vector3[MostUnits];
        readonly Vector3[] clusterMin = new Vector3[MostClusters], clusterMax = new Vector3[MostClusters];
        readonly float[] clusterDistance = new float[MostClusters];
        readonly Vector4[] clusterScreen = new Vector4[MostClusters];
        readonly int[] nearest = new int[MostClusters];
        // The tiles being laid out: light space bounds, texel class, corner, edge in texels, order by size.
        readonly Vector3[] tileMin = new Vector3[SunShadowTiles.Most], tileMax = new Vector3[SunShadowTiles.Most];
        readonly int[] tileLevel = new int[SunShadowTiles.Most], tileEdge = new int[SunShadowTiles.Most];
        readonly Vector2[] tileCorner = new Vector2[SunShadowTiles.Most];
        readonly Vector4[] tileScreen = new Vector4[SunShadowTiles.Most];
        readonly float[] tileDistance = new float[SunShadowTiles.Most];
        readonly int[] bySize = new int[SunShadowTiles.Most];
        int logged;

        // One cascade for Stride's own receiver, which nothing reads.
        public override LightShadowType GetShadowType(LightShadowMap shadowMap)
            => (base.GetShadowType(shadowMap) & ~(LightShadowType.CascadeMask | LightShadowType.BlendCascade | LightShadowType.DepthRangeAuto))
               | LightShadowType.Cascade1;

        public override LightShadowMapTexture CreateShadowMapTexture(RenderView renderView, RenderLight renderLight, IDirectLight light, int shadowMapSize)
        {
            var texture = base.CreateShadowMapTexture(renderView, renderLight, light, QualityLevels.ShadowSheet(Sheet));
            texture.CascadeCount = 1;
            lock (viewOf)
                viewOf[texture] = renderView;
            return texture;
        }

        RenderView ViewOf(LightShadowMapTexture texture)
        {
            lock (viewOf)
                return viewOf.GetValueOrDefault(texture);
        }

        public override void Reset(RenderContext context)
        {
            base.Reset(context);
            tilePool.Clear();
        }

        /// <summary>The view's tiles: casters joined into clusters, the nearest sized by their distance, packed
        /// onto the sheet, each with a shadow view of its own.</summary>
        public override void Collect(RenderContext context, RenderView sourceView, LightShadowMapTexture lightShadowMap)
        {
            var shadow = (LightDirectionalShadowMap)lightShadowMap.Shadow;
            SunShadowTiles tiles = tilePool.Add();
            lightShadowMap.ShaderData = tiles;
            Texture atlas = lightShadowMap.Atlas.Texture;
            tiles.Texture = atlas;
            tiles.DepthBias = shadow.BiasParameters.DepthBias;
            tiles.OffsetScale = shadow.BiasParameters.NormalOffsetScale;
            tiles.WorldToShadowCascadeUV[0] = Matrix.Identity;
            tiles.CascadeSplits[0] = sourceView.FarClipPlane;
            tiles.DepthRange[0] = new Vector2(0f, 1f);
            tiles.Count = 0;
            tiles.Sheet = lightShadowMap.GetRectangle(0);
            tiles.Used = Int2.Zero;
            tiles.Throw = Throw;
            if (Masks == null || !Masks.Wants(sourceView) || atlas == null)
                return;

            // Light space: x and y across the light, z along it (right-handed with -z as the view's depth).
            Vector3 along = Vector3.Normalize(lightShadowMap.RenderLight.Direction);
            Vector3 side = Vector3.Normalize(Vector3.Cross(MathF.Abs(along.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ, along));
            Vector3 up = Vector3.Cross(along, side);
            Vector3 right = Vector3.Cross(along, up);
            Vector3 eye = Matrix.Invert(sourceView.View).TranslationVector;
            float tangent = MathF.Tan(MathUtil.DegreesToRadians(SunAngle));
            int sheet = Math.Min(tiles.Sheet.Width, tiles.Sheet.Height), most = sheet / 2;
            float finest = Texel * QualityLevels.ShadowTexel;

            int clusters = Gather(context, eye, right, up, (most - 2 * Cell) * finest);
            int count = Visible(clusters, sourceView, right, up, along, tangent);

            // Texels about a pixel wide where the caster stands; tiles joined down to the most views, then
            // coarser from the far end until all fit on the sheet.
            float pixel = 2f / (MathF.Abs(sourceView.Projection.M22) * MathF.Max(sourceView.ViewSize.Y, 1f));
            long room = (long)(sheet / Cell) * (sheet / Cell), taken = 0;
            for (int t = 0; t < count; t++)
            {
                int c = nearest[t];
                Bounds(clusterMin[c], clusterMax[c], right, up, along, out tileMin[t], out tileMax[t]);
                (tileScreen[t], tileDistance[t]) = (clusterScreen[c], clusterDistance[c]);
                float wanted = pixel * clusterDistance[c] / Texel;
                Size(t, Math.Max(0, (int)MathF.Floor(MathF.Log2(MathF.Max(wanted, 1f)))), finest, most);
            }
            count = Join(count, Math.Clamp(Tiles, 1, SunShadowTiles.Most), finest, most);
            for (int t = 0; t < count; t++)
                taken += Cells(t);
            for (int pass = 0; taken > room && pass < 12; pass++)
                for (int t = count - 1; t >= 0 && taken > room; t--)
                    if (tileEdge[t] > Cell)
                    {
                        taken -= Cells(t);
                        Size(t, tileLevel[t] + 1, finest, most);
                        taken += Cells(t);
                    }
            if (taken > room)
                count = 0;

            // Largest first along a Z curve: squares of powers of two then pack without gaps.
            for (int t = 0; t < count; t++)
            {
                int at = t;
                for (; at > 0 && tileEdge[bySize[at - 1]] < tileEdge[t]; at--)
                    bySize[at] = bySize[at - 1];
                bySize[at] = t;
            }
            int cursor = 0;
            for (int n = 0; n < count; n++)
            {
                int t = bySize[n], edge = tileEdge[t], cells = edge / Cell;
                int x = Even(cursor) * Cell, y = Even(cursor >> 1) * Cell;
                cursor += cells * cells;
                tiles.Used = new Int2(Math.Max(tiles.Used.X, x + edge), Math.Max(tiles.Used.Y, y + edge));
                Place(context, sourceView, lightShadowMap, tiles, n, t, new Rectangle(tiles.Sheet.X + x, tiles.Sheet.Y + y, edge, edge),
                      right, up, along, finest * (1 << tileLevel[t]));
                tiles.Screens[n] = tileScreen[t];
            }
            tiles.Count = count;

            if (LogTiles && ++logged == 2000)
                for (int n = 0; n < count; n++)
                {
                    int t = bySize[n];
                    Console.WriteLine($"[shadow] tile {n}: {tileEdge[t]} texels of {finest * (1 << tileLevel[t]) * 100f:F2} cm, " +
                                      $"{tileDistance[t]:F1} m off, sheet {sheet}, used {tiles.Used.X}x{tiles.Used.Y}, clusters {clusters}");
                }
        }

        // The casters of the per-frame stage by model, joined where their boxes touch and stay under the
        // widest tile; returns the clusters' count.
        int Gather(RenderContext context, Vector3 eye, Vector3 right, Vector3 up, float widest)
        {
            int stage = ShadowCasterRenderStage.Index, units = 0;
            var objects = context.VisibilityGroup.RenderObjects;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RenderMesh { Enabled: true } mesh || mesh.ActiveRenderStages is not { } stages
                    || stage >= stages.Length || !stages[stage].Active || mesh.BoundingBox.Extent == Vector3.Zero)
                    continue;
                object owner = (object)mesh.RenderModel ?? mesh;
                Vector3 min = mesh.BoundingBox.Minimum, max = mesh.BoundingBox.Maximum;
                int u = 0;
                while (u < units && !ReferenceEquals(owners[u], owner))
                    u++;
                if (u < units)
                    (unitMin[u], unitMax[u]) = (Vector3.Min(unitMin[u], min), Vector3.Max(unitMax[u], max));
                else if (units < MostUnits)
                    (owners[units], unitMin[units], unitMax[units++]) = (owner, min, max);
            }
            Array.Clear(owners, 0, units);

            int clusters = 0;
            var touch = new Vector3(0.25f);
            for (int u = 0; u < units; u++)
            {
                Vector3 min = unitMin[u], max = unitMax[u];
                if (Away(min, max, eye) > Reach)
                    continue;
                int into = -1;
                for (int c = 0; c < clusters && into < 0; c++)
                    if (!Apart(clusterMin[c] - touch, clusterMax[c] + touch, min, max)
                        && Across(Vector3.Min(clusterMin[c], min), Vector3.Max(clusterMax[c], max), right, up) <= widest)
                        into = c;
                if (into >= 0)
                    (clusterMin[into], clusterMax[into]) = (Vector3.Min(clusterMin[into], min), Vector3.Max(clusterMax[into], max));
                else if (clusters < MostClusters)
                    (clusterMin[clusters], clusterMax[clusters++]) = (min, max);
            }
            for (int c = 0; c < clusters; c++)
                clusterDistance[c] = Away(clusterMin[c], clusterMax[c], eye);
            return clusters;
        }

        static float Away(Vector3 min, Vector3 max, Vector3 eye)
            => MathF.Max(Vector3.Distance((min + max) * 0.5f, eye) - Vector3.Distance(min, max) * 0.5f, 0f);

        static bool Apart(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax)
            => aMin.X > bMax.X || bMin.X > aMax.X || aMin.Y > bMax.Y || bMin.Y > aMax.Y || aMin.Z > bMax.Z || bMin.Z > aMax.Z;

        // The box's width across the light: the larger of its two extents there.
        static float Across(Vector3 min, Vector3 max, Vector3 right, Vector3 up)
        {
            Vector3 size = max - min;
            return MathF.Max(MathF.Abs(right.X) * size.X + MathF.Abs(right.Y) * size.Y + MathF.Abs(right.Z) * size.Z,
                             MathF.Abs(up.X) * size.X + MathF.Abs(up.Y) * size.Y + MathF.Abs(up.Z) * size.Z);
        }

        // The box in light space.
        static void Bounds(Vector3 min, Vector3 max, Vector3 right, Vector3 up, Vector3 along, out Vector3 low, out Vector3 high)
        {
            Vector3 centre = (min + max) * 0.5f, half = (max - min) * 0.5f;
            var c = new Vector3(Vector3.Dot(centre, right), Vector3.Dot(centre, up), Vector3.Dot(centre, along));
            var e = new Vector3(MathF.Abs(right.X) * half.X + MathF.Abs(right.Y) * half.Y + MathF.Abs(right.Z) * half.Z,
                                MathF.Abs(up.X) * half.X + MathF.Abs(up.Y) * half.Y + MathF.Abs(up.Z) * half.Z,
                                MathF.Abs(along.X) * half.X + MathF.Abs(along.Y) * half.Y + MathF.Abs(along.Z) * half.Z);
            (low, high) = (c - e, c + e);
        }

        // The clusters whose shadow (the box swept along the light, widening with the penumbra) meets the
        // view, nearest first, at most a sheet's tiles; notes the screen each reaches. Returns their count.
        int Visible(int clusters, RenderView view, Vector3 right, Vector3 up, Vector3 along, float tangent)
        {
            int count = 0;
            for (int c = 0; c < clusters; c++)
            {
                Bounds(clusterMin[c], clusterMax[c], right, up, along, out Vector3 low, out Vector3 high);
                float far = high.Z + Throw, wider = (far - low.Z) * tangent + 0.1f;
                bool behind = false;
                int left = 0, rightOf = 0, below = 0, above = 0, before = 0;
                var screen = new Vector4(1f, 1f, 0f, 0f);
                for (int k = 0; k < 8; k++)
                {
                    float pad = k < 4 ? 0.1f : wider;
                    Vector3 p = right * ((k & 1) == 0 ? low.X - pad : high.X + pad) + up * ((k & 2) == 0 ? low.Y - pad : high.Y + pad)
                                + along * (k < 4 ? low.Z : far);
                    Vector3.Transform(ref p, ref view.ViewProjection, out Vector4 clip);
                    left += clip.X < -clip.W ? 1 : 0;
                    rightOf += clip.X > clip.W ? 1 : 0;
                    below += clip.Y < -clip.W ? 1 : 0;
                    above += clip.Y > clip.W ? 1 : 0;
                    before += clip.Z < 0f ? 1 : 0;
                    if (clip.W < 0.05f)
                    {
                        behind = true;
                        continue;
                    }
                    var uv = new Vector2(0.5f + 0.5f * clip.X / clip.W, 0.5f - 0.5f * clip.Y / clip.W);
                    screen = new Vector4(MathF.Min(screen.X, uv.X), MathF.Min(screen.Y, uv.Y), MathF.Max(screen.Z, uv.X), MathF.Max(screen.W, uv.Y));
                }
                if (left == 8 || rightOf == 8 || below == 8 || above == 8 || before == 8)
                    continue;
                clusterScreen[c] = behind ? new Vector4(0f, 0f, 1f, 1f) : screen + new Vector4(-0.004f, -0.004f, 0.004f, 0.004f);
                int at = Math.Min(count, MostClusters - 1);
                for (; at > 0 && clusterDistance[nearest[at - 1]] > clusterDistance[c]; at--)
                    nearest[at] = nearest[at - 1];
                nearest[at] = c;
                count = Math.Min(count + 1, MostClusters);
            }
            return Math.Min(count, SunShadowTiles.Most);
        }

        // Tile t with texels of the class (at least) over its box.
        void Size(int t, int level, float finest, int most)
            => (tileLevel[t], tileCorner[t], tileEdge[t]) = Fit(tileMin[t], tileMax[t], level, finest, most);

        // A tile over a light space box with texels of the class (at least), coarser while its edge would pass
        // the most: the corner on the grid of the pyramid's coarse blocks, the edge the next power of two.
        static (int Level, Vector2 Corner, int Edge) Fit(Vector3 low, Vector3 high, int level, float finest, int most)
        {
            for (; ; level++)
            {
                float texel = finest * (1 << level), grid = Cell * texel, pad = 2f * texel + 0.02f;
                var corner = new Vector2(MathF.Floor((low.X - pad) / grid) * grid, MathF.Floor((low.Y - pad) / grid) * grid);
                float need = MathF.Max(high.X + pad - corner.X, high.Y + pad - corner.Y) / texel;
                int edge = Math.Max(Cell, MathUtil.NextPowerOfTwo((int)MathF.Ceiling(need)));
                if (edge <= most || level >= 24)
                    return (level, corner, edge);
            }
        }

        // Joins the two tiles that lose least (texel classes first, then sheet room) until at most the given
        // count is left, the joined one at the finer class; returns the count.
        int Join(int count, int most, float finest, int edgeMost)
        {
            for (; count > most; count--)
            {
                int keep = 0, gone = 1;
                long best = long.MaxValue;
                for (int a = 0; a < count; a++)
                    for (int b = a + 1; b < count; b++)
                    {
                        int finer = Math.Min(tileLevel[a], tileLevel[b]);
                        var (level, _, edge) = Fit(Vector3.Min(tileMin[a], tileMin[b]), Vector3.Max(tileMax[a], tileMax[b]), finer, finest, edgeMost);
                        long cost = ((long)(level - finer) << 40) + (long)(edge / Cell) * (edge / Cell) - Cells(a) - Cells(b);
                        if (cost < best)
                            (best, keep, gone) = (cost, a, b);
                    }
                int join = Math.Min(tileLevel[keep], tileLevel[gone]);
                (tileMin[keep], tileMax[keep]) = (Vector3.Min(tileMin[keep], tileMin[gone]), Vector3.Max(tileMax[keep], tileMax[gone]));
                Vector4 s = tileScreen[keep], o = tileScreen[gone];
                tileScreen[keep] = new Vector4(MathF.Min(s.X, o.X), MathF.Min(s.Y, o.Y), MathF.Max(s.Z, o.Z), MathF.Max(s.W, o.W));
                tileDistance[keep] = MathF.Min(tileDistance[keep], tileDistance[gone]);
                Size(keep, join, finest, edgeMost);
                for (int t = gone; t + 1 < count; t++)
                {
                    (tileMin[t], tileMax[t], tileLevel[t], tileCorner[t]) = (tileMin[t + 1], tileMax[t + 1], tileLevel[t + 1], tileCorner[t + 1]);
                    (tileEdge[t], tileScreen[t], tileDistance[t]) = (tileEdge[t + 1], tileScreen[t + 1], tileDistance[t + 1]);
                }
            }
            return count;
        }

        long Cells(int t) => (long)(tileEdge[t] / Cell) * (tileEdge[t] / Cell);

        // Every second bit of a Z curve's index.
        static int Even(int index)
        {
            int value = 0;
            for (int bit = 0; bit < 12; bit++)
                value |= (index >> (2 * bit) & 1) << bit;
            return value;
        }

        // Slot n takes tile t in the atlas' rectangle: its matrices and its shadow view, which draws only what
        // lies within the tile's depth (the stage clips), so a caster nearer the sun keeps its own penumbra.
        void Place(RenderContext context, RenderView sourceView, LightShadowMapTexture lightShadowMap, SunShadowTiles tiles, int n, int t,
                   Rectangle rectangle, Vector3 right, Vector3 up, Vector3 along, float texel)
        {
            float near = tileMin[t].Z - 0.05f, range = tileMax[t].Z + 0.05f - near, edge = rectangle.Width * texel;
            var view = new Matrix(right.X, up.X, -along.X, 0f, right.Y, up.Y, -along.Y, 0f, right.Z, up.Z, -along.Z, 0f, 0f, 0f, near, 1f);
            Matrix projection = Matrix.OrthoOffCenterRH(tileCorner[t].X, tileCorner[t].X + edge, tileCorner[t].Y, tileCorner[t].Y + edge, 0f, range);
            Matrix viewProjection = view * projection;
            float width = tiles.Texture.Width, height = tiles.Texture.Height;
            tiles.Rects[n] = new Vector4(rectangle.Left / width, rectangle.Top / height, rectangle.Right / width, rectangle.Bottom / height);
            tiles.WorldToUV[n] = viewProjection * Matrix.Scaling(0.5f * rectangle.Width / width, -0.5f * rectangle.Height / height, 1f)
                                 * Matrix.Translation(0.5f * (tiles.Rects[n].X + tiles.Rects[n].Z), 0.5f * (tiles.Rects[n].Y + tiles.Rects[n].W), 0f);
            tiles.Scales[n] = new Vector4(Math.Min(FigureShadowMasks.Levels - 1, System.Numerics.BitOperations.Log2((uint)rectangle.Width) - 1), range, texel, 0f);

            ShadowMapRenderView shadowView = CreateRenderView();
            shadowView.RenderView = sourceView;
            shadowView.ShadowMapTexture = lightShadowMap;
            shadowView.Rectangle = rectangle;
            shadowView.View = view;
            shadowView.Projection = projection;
            shadowView.ViewProjection = viewProjection;
            shadowView.ViewSize = new Vector2(rectangle.Width, rectangle.Height);
            shadowView.NearClipPlane = 0f;
            shadowView.FarClipPlane = range;
            shadowView.VisiblityIgnoreDepthPlanes = false;
            context.RenderSystem.Views.Add(shadowView);
        }

        public override ILightShadowMapShaderGroupData CreateShaderGroupData(LightShadowType shadowType)
            => new Receiver(this, (LightShadowMapShaderGroupDataBase)base.CreateShaderGroupData(shadowType));

        /// <summary>Stride's group data, with the receiver shader swapped and its parameters added.</summary>
        sealed class Receiver(SunShadowMapRenderer owner, LightShadowMapShaderGroupDataBase inner) : ILightShadowMapShaderGroupData
        {
            ValueParameterKey<Vector2> maskSizeKey;
            ObjectParameterKey<Texture> maskKey;
            ValueParameterKey<float> tangentKey;
            ValueParameterKey<float> penumbraKey;
            ValueParameterKey<Matrix> staticUVKey;
            ValueParameterKey<Vector2> staticTexelKey;
            ObjectParameterKey<Texture> staticMapKey;
            ObjectParameterKey<Texture> sunLightmapKey;
            ValueParameterKey<Vector2> sunLightmapTexelKey;
            ValueParameterKey<Vector4> cloudLayerKey, cloudScaleKey, cloudOffsetKey;
            ValueParameterKey<Vector2> cloudWindKey;
            ValueParameterKey<Vector3> cloudSunKey;
            ObjectParameterKey<Texture> cloudMapKey;

            public void ApplyShader(ShaderMixinSource mixin) => inner.ApplyShader(mixin);

            public void UpdateLayout(string compositionName)
            {
                inner.UpdateLayout(compositionName);
                maskSizeKey = FigureShadowSize.ComposeWith(compositionName);
                maskKey = FigureShadow.ComposeWith(compositionName);
                tangentKey = SunTangent.ComposeWith(compositionName);
                penumbraKey = MaxPenumbraKey.ComposeWith(compositionName);
                staticUVKey = WorldToStaticUV.ComposeWith(compositionName);
                staticTexelKey = StaticTexelSize.ComposeWith(compositionName);
                staticMapKey = StaticShadowMap.ComposeWith(compositionName);
                sunLightmapKey = SunLightmap.ComposeWith(compositionName);
                sunLightmapTexelKey = SunLightmapTexelSize.ComposeWith(compositionName);
                cloudLayerKey = CloudShadowLayer.ComposeWith(compositionName);
                cloudScaleKey = CloudShadowScale.ComposeWith(compositionName);
                cloudOffsetKey = CloudShadowOffset.ComposeWith(compositionName);
                cloudWindKey = CloudShadowWind.ComposeWith(compositionName);
                cloudSunKey = CloudShadowToSun.ComposeWith(compositionName);
                cloudMapKey = CloudShadowMap.ComposeWith(compositionName);
            }

            public void UpdateLightCount(int lightLastCount, int lightCurrentCount)
            {
                inner.UpdateLightCount(lightLastCount, lightCurrentCount);
                var mixins = inner.ShadowShader.Mixins;
                var stride = mixins[0];
                mixins[0] = new ShaderClassSource("CascadeShadowReceiver") { GenericArguments = stride.GenericArguments };
            }

            public void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters,
                Stride.Core.Collections.FastListStruct<Stride.Rendering.Lights.LightDynamicEntry> currentLights)
            {
                inner.ApplyViewParameters(context, parameters, currentLights);
                float tangent = MathF.Tan(MathUtil.DegreesToRadians(owner.SunAngle));
                parameters.Set(tangentKey, tangent);
                parameters.Set(penumbraKey, owner.MaxPenumbra);

                // The static map once it is complete; until then (or never) the atlas stands in, unread.
                var still = context.RenderContext.Tags.Get(StaticSunShadowRenderer.Key);
                bool ready = still is { Ready: true, Texture: not null };
                var sheet = currentLights.Count > 0 ? currentLights[0].ShadowMapTexture.ShaderData as SunShadowTiles : null;
                Texture stand = sheet?.Texture;
                parameters.Set(staticMapKey, ready ? still.Texture : stand);

                // The moving casters' shadow of the first light, drawn per pixel for this view.
                Texture mask = null;
                if (owner.Masks != null && sheet != null && owner.ViewOf(currentLights[0].ShadowMapTexture) is { } view)
                    mask = owner.Masks.Bind(view, sheet, tangent);
                parameters.Set(maskKey, mask ?? stand);
                parameters.Set(maskSizeKey, mask != null ? new Vector2(mask.Width, mask.Height) : Vector2.Zero);
                var sunmap = context.RenderContext.Tags.Get(Stride.Rendering.Voxels.ForwardRendererVoxels.SunLightmapKey) ?? stand;
                parameters.Set(sunLightmapKey, sunmap);
                parameters.Set(sunLightmapTexelKey, sunmap != null ? new Vector2(1f / sunmap.Width, 1f / sunmap.Height) : Vector2.Zero);
                parameters.Set(staticUVKey, ready ? still.WorldToUV : Matrix.Identity);
                parameters.Set(staticTexelKey, ready ? new Vector2(1f / still.Texture.Width) : Vector2.Zero);

                // The clouds' shadow; strength 0 (and the atlas standing in, unread) without them.
                var air = context.RenderContext.Tags.Get(Atmosphere.Key);
                bool clouds = air is { CloudMap: not null, CloudShadow: > 0f } && currentLights.Count > 0;
                parameters.Set(cloudMapKey, clouds ? air.CloudMap : stand);
                parameters.Set(cloudScaleKey, clouds ? new Vector4(1f / air.CloudTile, 1f / air.BandTile, air.CloudShadow, 0f) : Vector4.Zero);
                if (!clouds)
                    return;
                parameters.Set(cloudLayerKey, new Vector4(air.CloudBase, air.CloudThickness, air.CloudCoverage, air.CloudDensity));
                parameters.Set(cloudOffsetKey, air.CloudOffset);
                parameters.Set(cloudWindKey, air.WindAxis);
                parameters.Set(cloudSunKey, -Vector3.Normalize(currentLights[0].Light.Direction));
            }

            public void ApplyDrawParameters(RenderDrawContext context, ParameterCollection parameters,
                Stride.Core.Collections.FastListStruct<Stride.Rendering.Lights.LightDynamicEntry> currentLights,
                ref BoundingBoxExt boundingBox)
                => inner.ApplyDrawParameters(context, parameters, currentLights, ref boundingBox);
        }
    }
}
