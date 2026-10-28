using KotH.Shared.Combat;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.UI;
using Stride.UI.Controls;

namespace KotH.Client.Hud
{
    /// <summary>Shows a body's damage percent in the world above its head. Presentation
    /// only: it reads the value and never writes one. It interpolates its own position
    /// instead of following the rendered capsule, so no update order between the two
    /// matters.</summary>
    [DataContract("KotH.DamageLabel")]
    public sealed class DamageLabel : SyncScript
    {
        /// <userdoc>Height above the crown, in metres.</userdoc>
        [Display("Offset", "Display")] public float Offset = 0.35f;

        /// <userdoc>Font size in metres. Unity's 1.2 in TextMeshPro points.</userdoc>
        [Display("Font size", "Display")] public float FontSize = 0.12f;

        [Display("Colour", "Display")] public Color Color = Color.White;
        [Display("Font", "Display")] public SpriteFont Font;

        [DataMemberIgnore] public DamagePercent Target { get; set; }
        [DataMemberIgnore] public PlayerMotor Motor { get; set; }

        /// <summary>Shown before the percent, for other players; null for none.</summary>
        [DataMemberIgnore] public string Name { get; set; }

        /// <summary>The camera the text turns towards.</summary>
        [DataMemberIgnore] public TransformComponent Camera { get; set; }

        // Unity's rect: 4 x 1 m, wide enough for "1000.0 %" without a break.
        static readonly Vector2 RectMeters = new Vector2(4f, 1f);

        // Virtual pixels per metre; only the distance field's sharpness
        // depends on it, the size on screen does not.
        const float PixelsPerMeter = 200f;

        TextBlock text;
        float shown = float.NaN;

        public DamageLabel()
        {
            // After the camera, which moves at 1000: the text faces the
            // position the frame is drawn from.
            Priority = 1100;
        }

        /// <summary>What the label shows right now.</summary>
        [DataMemberIgnore] public string Text => text?.Text;

        public override void Start()
        {
            text = new TextBlock
            {
                Font = Font,
                TextSize = FontSize * PixelsPerMeter,
                TextColor = Color,
                TextAlignment = TextAlignment.Center,
                WrapText = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CanBeHitByUser = false,
            };

            Entity.Add(new UIComponent
            {
                Page = new UIPage { RootElement = text },
                IsFullScreen = false,
                IsBillboard = false,
                Size = new Vector3(RectMeters, 1f),
                Resolution = new Vector3(RectMeters * PixelsPerMeter, 1000f),
                ResolutionStretch = ResolutionStretch.FixedWidthFixedHeight,
            });

            if (Target == null || Motor == null || Font == null)
                Log.Warning("DamageLabel without target or font stays empty.");
        }

        public override void Update()
        {
            if (Target == null || Motor == null || text == null)
                return;

            // The same formula the visual system uses, rather than reading
            // the drawn capsule.
            Vector3 center = UMath.Lerp(Motor.PreviousBodyCenter, Motor.BodyCenter, TickInterpolation.Alpha);
            Vector3 position = center + Vector3.UnitY * (Motor.Height * 0.5f + Offset);
            Entity.Transform.Position = position;

            if (Camera != null)
                Entity.Transform.Rotation = Facing(Camera.Position - position);

            // Rewritten only when the number moved, so a still dummy costs
            // nothing per frame.
            float value = Target.Percent;
            if (value != shown)
            {
                shown = value;
                string percent = value.ToString("F1", HudRenderer.Numbers) + " %";
                text.Text = Name != null ? Name + "  " + percent : percent;
            }
        }

        /// <summary>
        /// Width and height of s in metres, as this label would draw it.
        /// The probe's font check: a missing glyph set measures zero.
        /// </summary>
        public Vector2 Measure(string s)
        {
            if (Font == null)
                return Vector2.Zero;
            // Measured at the font's own size and scaled: the size argument
            // of MeasureString does not scale a distance field font.
            return Font.MeasureString(s) * (FontSize / Font.Size);
        }

        /// <summary>
        /// Turns the UI plane, which faces +Z, to look at the camera: Unity's
        /// LookRotation(position - camera) with the world's up.
        /// </summary>
        static Quaternion Facing(Vector3 toCamera)
        {
            Vector3 back = Vector3.Normalize(toCamera);
            Vector3 right = Vector3.Cross(Vector3.UnitY, back);
            if (right.LengthSquared() < 1e-8f)
                return Quaternion.Identity;
            right.Normalize();

            Matrix m = Matrix.Identity;
            m.Right = right;
            m.Up = Vector3.Cross(back, right);
            m.Backward = back;
            return Quaternion.RotationMatrix(m);
        }
    }
}
