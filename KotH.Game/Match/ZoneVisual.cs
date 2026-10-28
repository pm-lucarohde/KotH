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
    /// <summary>Draws the capture zone as glowing rails along its floor edges, with motes rising off them, in the
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
        bool built;
        ZoneLook shown = (ZoneLook)(-1);

        public override void Update()
        {
            if (Zone == null || Match == null || Cube == null)
                return;

            if (!built)
            {
                Build();
                built = true;
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

        void Build()
        {
            Entity.Transform.Position = Zone.Entity.Transform.Position;
            Entity.Transform.Rotation = Zone.Entity.Transform.Rotation;

            Vector3 s = Zone.Size;
            float bottom = -s.Y * 0.5f + Lift;
            float y = bottom + RailHeight * 0.5f;
            float inX = (s.X - Thickness) * 0.5f, inZ = (s.Z - Thickness) * 0.5f;

            Part(new Vector3(0f, y, -inZ), new Vector3(s.X, RailHeight, Thickness));
            Part(new Vector3(0f, y, inZ), new Vector3(s.X, RailHeight, Thickness));
            Part(new Vector3(-inX, y, 0f), new Vector3(Thickness, RailHeight, s.Z - 2f * Thickness));
            Part(new Vector3(inX, y, 0f), new Vector3(Thickness, RailHeight, s.Z - 2f * Thickness));

            AddMotes(new Vector3(0f, bottom, -inZ), 0f, s.X);
            AddMotes(new Vector3(0f, bottom, inZ), 0f, s.X);
            AddMotes(new Vector3(-inX, bottom, 0f), MathUtil.PiOverTwo, s.Z);
            AddMotes(new Vector3(inX, bottom, 0f), MathUtil.PiOverTwo, s.Z);
        }

        // One side's motes: the prefab's metre of rail stretched to its length,
        // as many per second for every metre. Before it joins the scene, so
        // the emitters size their pools for it.
        void AddMotes(Vector3 at, float turn, float length)
        {
            if (Motes == null)
                return;
            foreach (Entity side in Motes.Instantiate())
            {
                side.Transform.Position = at;
                side.Transform.Rotation = Quaternion.RotationY(turn);
                if (side.Get<ParticleSystemComponent>() is { } particles)
                {
                    particles.RenderGroup = RailGroup;
                    foreach (ParticleEmitter emitter in particles.ParticleSystem.Emitters)
                        Stretch(emitter, length);
                    motes.Add(particles);
                }
                Entity.AddChild(side);
            }
        }

        static void Stretch(ParticleEmitter emitter, float length)
        {
            foreach (ParticleSpawner spawner in emitter.Spawners)
                if (spawner is SpawnerPerSecond rate)
                    rate.SpawnCount *= length;
            foreach (ParticleInitializer initializer in emitter.Initializers)
                if (initializer is InitialPositionSeed seed)
                {
                    seed.PositionMin = new Vector3(seed.PositionMin.X * length, seed.PositionMin.Y, seed.PositionMin.Z);
                    seed.PositionMax = new Vector3(seed.PositionMax.X * length, seed.PositionMax.Y, seed.PositionMax.Z);
                }
        }

        void Part(Vector3 at, Vector3 scale)
        {
            // Light, not a solid: nothing of the zone throws a shadow.
            var model = new ModelComponent(Cube) { IsShadowCaster = false, RenderGroup = RailGroup };
            var part = new Entity("ZonePart") { model };
            part.Transform.Position = at;
            part.Transform.Scale = scale;
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
