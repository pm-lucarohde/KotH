using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;

namespace KotH.Client.Hud
{
    /// <summary>
    /// A dot with four diagonal arms, drawn in code so it needs no sprite
    /// asset. Presentation only: it reads nothing and is never read. Sizes
    /// are in pixels and stay in pixels, like the performance readout --
    /// an aiming mark that grows with the window is a different mark.
    /// </summary>
    [DataContract("KotH.Crosshair")]
    public sealed class Crosshair : StartupScript, IHudElement
    {
        [Display("Farbe", "Farben")] public Color Color = Color.White;

        // A dark backing behind every part, so the mark stays readable on
        // sky and on pale rock without a second mechanism.
        [Display("Umrissfarbe", "Farben")] public Color OutlineColor = new Color(0f, 0f, 0f, 0.65f);
        [Display("Umriss", "Farben")] public float Outline = 1f;

        [Display("Punktgröße", "Maße (Pixel)")] public float DotSize = 3f;
        [Display("Armlänge", "Maße (Pixel)")] public float ArmLength = 7f;
        [Display("Armdicke", "Maße (Pixel)")] public float ArmThickness = 2f;

        /// <userdoc>Abstand von der Mitte bis zum Beginn eines Arms</userdoc>
        [Display("Lücke", "Maße (Pixel)")] public float Gap = 4f;

        /// <summary>Not drawn, such as while the main menu is open.</summary>
        [DataMemberIgnore] public bool Hidden { get; set; }

        // 32 px is far more than the mark is ever drawn at, so the disc
        // stays smooth however large the dot is set.
        const int DiscPixels = 32;

        static readonly float[] Angles = { 45f, 135f, 225f, 315f };

        struct Part
        {
            public Texture Texture;
            public Vector2 Offset;
            public float Angle;
            public Vector2 Size;
            public Color4 Tint;
        }

        readonly List<Part> parts = new List<Part>();
        HudRenderer hud;
        Texture disc;

        public override void Start()
        {
            hud = HudRenderer.Of(this);
            disc = MakeDisc(GraphicsDevice);

            AddPart(disc, Vector2.Zero, 0f, new Vector2(DotSize, DotSize));

            for (int i = 0; i < Angles.Length; i++)
            {
                // The screen counts y downwards, Unity's canvas upwards: the
                // same arm mirrored, so position and turn both flip.
                float rad = MathUtil.DegreesToRadians(Angles[i]);
                var dir = new Vector2(MathF.Cos(rad), -MathF.Sin(rad));
                AddPart(hud.White, dir * (Gap + ArmLength * 0.5f), -rad, new Vector2(ArmLength, ArmThickness));
            }

            hud.Add(this);
        }

        public override void Cancel()
        {
            hud?.Remove(this);
            disc?.Dispose();
        }

        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            if (Hidden)
                return;
            Vector2 center = screen * 0.5f;

            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                var at = new RectangleF(center.X + p.Offset.X, center.Y + p.Offset.Y, p.Size.X, p.Size.Y);
                var origin = new Vector2(p.Texture.Width * 0.5f, p.Texture.Height * 0.5f);
                batch.Draw(p.Texture, at, null, p.Tint, p.Angle, origin);
            }
        }

        /// <summary>
        /// One part of the mark: a dark rectangle a hair larger, and the
        /// bright one on top of it. Added in that order, because the HUD
        /// draws back to front.
        /// </summary>
        void AddPart(Texture texture, Vector2 offset, float angle, Vector2 size)
        {
            if (Outline > 0f)
                parts.Add(new Part { Texture = texture, Offset = offset, Angle = angle,
                                     Size = size + new Vector2(Outline * 2f), Tint = Premultiplied(OutlineColor) });

            parts.Add(new Part { Texture = texture, Offset = offset, Angle = angle, Size = size, Tint = Premultiplied(Color) });
        }

        static Color4 Premultiplied(Color c) => Color4.PremultiplyAlpha(c.ToColor4());

        /// <summary>
        /// A filled circle with one pixel of soft edge, built once. Without
        /// the soft edge the dot reads as a tiny square at small sizes.
        /// Premultiplied, as the batch blends.
        /// </summary>
        static Texture MakeDisc(GraphicsDevice device)
        {
            var data = new byte[DiscPixels * DiscPixels * 4];
            float r = DiscPixels * 0.5f;

            for (int y = 0; y < DiscPixels; y++)
            {
                for (int x = 0; x < DiscPixels; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float alpha = MathUtil.Clamp(r - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                    byte v = (byte)MathF.Round(alpha * 255f);

                    int i = (y * DiscPixels + x) * 4;
                    data[i] = data[i + 1] = data[i + 2] = data[i + 3] = v;
                }
            }

            return Texture.New2D(device, DiscPixels, DiscPixels, PixelFormat.R8G8B8A8_UNorm, data);
        }
    }

}
