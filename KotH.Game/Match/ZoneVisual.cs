using System;
using System.Collections.Generic;
using KotH.Client.Hud;
using KotH.Shared.Match;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles;
using Stride.Particles.Components;
using Stride.Particles.Initializers;
using Stride.Particles.Spawners;
using Stride.Rendering;
using Stride.Rendering.Materials;

namespace KotH.Client.Match
{
    /// <summary>Draws the capture zone as glowing rails along its floor outline, with motes rising off them, in the
    /// colour of what the zone does for this screen. Built from the zone's box once the game root has wired it, so
    /// moving the zone in the arena moves the rails along. Presentation only.</summary>
    [DataContract("KotH.ZoneVisual")]
    public sealed class ZoneVisual : SyncScript
    {
        /// <userdoc>A 1 m cube the rails are built from, with their material.</userdoc>
        [Display("Cube", "Visuals")] public Model Cube;

        /// <userdoc>Particles rising from the rails in the zone's colour. Their emitters are set up for 1 m of edge (edge along X, height from the zone floor) and stretched to each side.</userdoc>
        [Display("Edge particles", "Visuals")] public Prefab Motes;

        [Display("Rail width", "Size")] public float Thickness = 0.12f;
        [Display("Rail height", "Size")] public float RailHeight = 0.1f;

        /// <userdoc>How far the particles are lightened towards white, from 0 (zone colour) to 1 (white).</userdoc>
        [Display("Lighten particles", "Visuals")] public float MoteLight = 0f;

        [DataMemberIgnore] public CaptureZone Zone { get; set; }
        [DataMemberIgnore] public HillMatch Match { get; set; }
        [DataMemberIgnore] public SimulationBody Self { get; set; }

        /// <summary>The HUD's colours, so rails and status text always agree.</summary>
        [DataMemberIgnore] public HillBoard Board { get; set; }

        // A hair above the zone's floor, which lies on the plateau's top.
        const float Lift = 0.002f;

        /// <summary>Drawn, but in none of the voxel GI's groups: the rails never move and give no GI.</summary>
        const RenderGroup RailGroup = RenderGroup.Group4;

        readonly List<ParticleSystemComponent> motes = new List<ParticleSystemComponent>();
        readonly List<Entity> pieces = new List<Entity>();
        // The outline's stretches the motes rise from: middle on the floor, turn from +X, length.
        readonly List<(Vector3 At, float Turn, float Length)> stretches = new();
        CaptureZone builtFor;
        ZoneLook shown = (ZoneLook)(-1);

        public override void Update()
        {
            if (Zone == null || Match == null || Cube == null)
                return;

            // A map change in-process wires another zone: the old rails go (Luca: the lobby's
            // square stayed in Volcano's crater).
            if (Zone != builtFor)
            {
                Clear();
                Build();
                builtFor = Zone;
                shown = (ZoneLook)(-1);
            }

            ZoneLook look = HillBoard.Look(Match.State, Self, Match.WarmupTicks);
            if (look == shown)
                return;
            shown = look;

            Color4 color = (Board?.ColorOf(look) ?? Color.White).ToColor4();
            if (Cube.Materials.Count > 0)
                Tint(Cube.Materials[0].Material, color);
            // The rails glow in linear colour, the particles' shade is sRGB:
            // the same colour as the rails, lifted a little towards white.
            Color4 light = Color4.Lerp(color.ToSRgb(), Color.White.ToColor4(), MoteLight);
            light.A = 1f;
            foreach (ParticleSystemComponent particles in motes)
                particles.Color = light;
        }

        void Clear()
        {
            foreach (Entity piece in pieces)
                Entity.RemoveChild(piece);
            pieces.Clear();
            motes.Clear();
            stretches.Clear();
        }

        void Build()
        {
            Entity.Transform.Position = Zone.Entity.Transform.Position;
            Entity.Transform.Rotation = Zone.Entity.Transform.Rotation;

            Vector3 s = Zone.Size;
            float bottom = -s.Y * 0.5f + Lift;
            float y = bottom + RailHeight * 0.5f;
            if (Zone.Shape == ZoneShape.Cylinder || Zone.Shape == ZoneShape.Polygon && Zone.Corners.Count >= 3)
            {
                Outline(Corners(s), bottom, y);
                // A ring's hole: its rail too, wound the other way so it lies in the ring.
                if (Zone.Shape == ZoneShape.Cylinder && Zone.Hole > 0f)
                {
                    List<Vector2> hole = Ellipse(Zone.Hole, Zone.Hole);
                    hole.Reverse();
                    Outline(hole, bottom, y);
                }
                AddMotes();
                return;
            }
            float inX = (s.X - Thickness) * 0.5f, inZ = (s.Z - Thickness) * 0.5f;

            Part(new Vector3(0f, y, -inZ), new Vector3(s.X, RailHeight, Thickness));
            Part(new Vector3(0f, y, inZ), new Vector3(s.X, RailHeight, Thickness));
            Part(new Vector3(-inX, y, 0f), new Vector3(Thickness, RailHeight, s.Z - 2f * Thickness));
            Part(new Vector3(inX, y, 0f), new Vector3(Thickness, RailHeight, s.Z - 2f * Thickness));

            stretches.Add((new Vector3(0f, bottom, -inZ), 0f, s.X));
            stretches.Add((new Vector3(0f, bottom, inZ), 0f, s.X));
            stretches.Add((new Vector3(-inX, bottom, 0f), MathUtil.PiOverTwo, s.Z));
            stretches.Add((new Vector3(inX, bottom, 0f), MathUtil.PiOverTwo, s.Z));
            AddMotes();
        }

        /// <summary>The floor outline: the drawn corners, or the ellipse in segments of about half a metre.</summary>
        List<Vector2> Corners(Vector3 s)
        {
            if (Zone.Shape == ZoneShape.Polygon)
                return new List<Vector2>(Zone.Corners);
            return Ellipse(s.X * 0.5f, s.Z * 0.5f);
        }

        static List<Vector2> Ellipse(float rx, float rz)
        {
            int n = Math.Max(24, (int)MathF.Ceiling(MathUtil.TwoPi * Math.Max(rx, rz) / 0.5f));
            var ring = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                float a = MathUtil.TwoPi * i / n;
                ring.Add(new Vector2(rx * MathF.Cos(a), rz * MathF.Sin(a)));
            }
            return ring;
        }

        // A rail along every edge, half its width inside, long enough to close the
        // joints. Motes per stretch of about 2 m, so a round zone keeps few emitters.
        void Outline(List<Vector2> c, float bottom, float y)
        {
            float area = 0f;
            for (int i = 0, j = c.Count - 1; i < c.Count; j = i++)
                area += c[j].X * c[i].Y - c[i].X * c[j].Y;
            float inward = area > 0f ? 1f : -1f;
            int from = 0;
            float run = 0f;
            for (int i = 0; i < c.Count; i++)
            {
                Vector2 a = c[i], b = c[(i + 1) % c.Count], d = b - a;
                float length = d.Length();
                if (length < 1e-4f)
                    continue;
                Vector2 n = new Vector2(-d.Y, d.X) / length * (inward * Thickness * 0.5f);
                Vector2 mid = (a + b) * 0.5f + n;
                Quaternion turn = Quaternion.RotationY(MathF.Atan2(-d.Y, d.X));
                Part(new Vector3(mid.X, y, mid.Y), new Vector3(length + Thickness, RailHeight, Thickness), turn);
                run += length;
                if (run >= 2f || i == c.Count - 1)
                {
                    Vector2 p = c[from], q = b, e = q - p, at = (p + q) * 0.5f + n;
                    stretches.Add((new Vector3(at.X, bottom, at.Y), MathF.Atan2(-e.Y, e.X), e.Length()));
                    from = i + 1;
                    run = 0f;
                }
            }
        }

        // One set of motes for the whole outline: the prefab's metre of rail, its seeds spread over every
        // stretch and as many per second for every metre. Before it joins the scene, so the emitters size
        // their pools for it.
        void AddMotes()
        {
            if (Motes == null || stretches.Count == 0)
                return;
            var centres = new Vector3[stretches.Count];
            var turns = new float[stretches.Count];
            var lengths = new float[stretches.Count];
            for (int i = 0; i < stretches.Count; i++)
                (centres[i], turns[i], lengths[i]) = stretches[i];
            foreach (Entity side in Motes.Instantiate())
            {
                if (side.Get<ParticleSystemComponent>() is { } particles)
                {
                    particles.RenderGroup = RailGroup;
                    foreach (ParticleEmitter emitter in particles.ParticleSystem.Emitters)
                        Spread(emitter, centres, turns, lengths);
                    motes.Add(particles);
                }
                pieces.Add(side);
                Entity.AddChild(side);
            }
        }

        static void Spread(ParticleEmitter emitter, Vector3[] centres, float[] turns, float[] lengths)
        {
            float length = 0f;
            for (int i = 0; i < emitter.Initializers.Count; i++)
                if (emitter.Initializers[i] is InitialPositionSeed seed)
                {
                    var outline = new OutlineSeed(seed, centres, turns, lengths);
                    length = outline.Length;
                    emitter.Initializers[i] = outline;
                }
            foreach (ParticleSpawner spawner in emitter.Spawners)
                if (spawner is SpawnerPerSecond rate)
                    rate.SpawnCount *= length;
        }

        void Part(Vector3 at, Vector3 scale) => Part(at, scale, Quaternion.Identity);

        void Part(Vector3 at, Vector3 scale, Quaternion turn)
        {
            // Light, not a solid: nothing of the zone throws a shadow.
            var model = new ModelComponent(Cube) { IsShadowCaster = false, RenderGroup = RailGroup };
            var part = new Entity("ZonePart") { model };
            part.Transform.Position = at;
            part.Transform.Rotation = turn;
            part.Transform.Scale = scale;
            pieces.Add(part);
            Entity.AddChild(part);
        }

        /// <summary>Sets a material's glow; the colour is a pass parameter, no shader rebuild.</summary>
        static void Tint(Material material, Color4 color)
        {
            if (material == null)
                return;
            foreach (MaterialPass pass in material.Passes)
                pass.Parameters.Set(MaterialKeys.EmissiveValue, color);
        }
    }
}
