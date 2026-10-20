using KotH.Client.Hud;
using KotH.Shared.Match;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Materials;

namespace KotH.Client.Match
{
    /// <summary>
    /// Draws the capture zone as glowing rails along its floor edges, in
    /// the colour of what the zone does for this screen. Built from the zone's box once the game root has wired it, so
    /// moving the zone in the arena moves the rails along. Presentation only.
    /// </summary>
    [DataContract("KotH.ZoneVisual")]
    public sealed class ZoneVisual : SyncScript
    {
        /// <userdoc>Ein Würfel von 1 m, aus dem die Leisten gebaut werden, mit ihrem Material.</userdoc>
        [Display("Würfel", "Darstellung")] public Model Cube;

        [Display("Leistenbreite", "Maße")] public float Thickness = 0.12f;
        [Display("Leistenhöhe", "Maße")] public float RailHeight = 0.1f;

        [DataMemberIgnore] public CaptureZone Zone { get; set; }
        [DataMemberIgnore] public HillMatch Match { get; set; }
        [DataMemberIgnore] public SimulationBody Self { get; set; }

        /// <summary>The HUD's colours, so rails and status text always agree.</summary>
        [DataMemberIgnore] public HillBoard Board { get; set; }

        // A hair above the zone's floor, which lies on the plateau's top.
        const float Lift = 0.002f;

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

            ZoneLook look = HillBoard.Look(Match.State, Self);
            if (look == shown)
                return;
            shown = look;

            Color4 color = (Board?.ColorOf(look) ?? Color.White).ToColor4();
            if (Cube.Materials.Count > 0)
                Tint(Cube.Materials[0].Material, color);
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
        }

        void Part(Vector3 at, Vector3 scale)
        {
            // Light, not a solid: nothing of the zone throws a shadow.
            var model = new ModelComponent(Cube) { IsShadowCaster = false };
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
