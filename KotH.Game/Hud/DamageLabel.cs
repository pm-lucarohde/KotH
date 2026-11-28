using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using KotH.Client.Audio;
using KotH.Client.Localization;
using KotH.Client.Menu;
using KotH.Shared.Combat;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Sprites;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaTextAlignment = Avalonia.Media.TextAlignment;
using Color = Stride.Core.Mathematics.Color;
using Matrix = Stride.Core.Mathematics.Matrix;
using Thickness = Stride.UI.Thickness;

namespace KotH.Client.Hud
{
    /// <summary>Shows a body's damage percent and name in the world above its head. Uses Stride's crisp SDF
    /// TextBlock whenever all glyphs exist in Font, and falls back to a cached high-DPI Avalonia/Skia texture
    /// (Accuratist + Noto Sans fallbacks) for non-Latin scripts such as CJK, Arabic, Cyrillic, Hindi or Thai.</summary>
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

        /// <summary>The player's NetId, whose voice tints the label while it is heard; 0 for none.</summary>
        [DataMemberIgnore] public int NetId { get; set; }

        /// <summary>The body this screen plays; its team mates' labels show in the own colour.</summary>
        public static SimulationBody Self { get; set; }

        /// <summary>The own colour (HillBoard's own zone), for the team mates' labels.</summary>
        public static readonly Color MateColor = new Color(30, 200, 230);

        /// <summary>A catcher's label in 1, 2, 3, Free (HillBoard's enemy red).</summary>
        public static readonly Color CatcherColor = new Color(255, 85, 70);

        /// <summary>The camera the text turns towards.</summary>
        [DataMemberIgnore] public TransformComponent Camera { get; set; }

        // World plane wide enough for a 32-char name, damage percent, and speaker icon.
        static readonly Vector2 RectMeters = new Vector2(4f, 1f);
        const float PixelsPerMeter = 200f;
        const int Supersample = 4;
        const int MaxTexWidth = 2048;
        const int MaxTexHeight = 256;
        const double MaxNamePx = 1400;
        const int Pad = 12;

        static readonly FontFamily LabelFont = new FontFamily(
            "avares://KotH.Game/Menu/Fonts#Accuratist, avares://KotH.Game/Menu/Fonts#Noto Sans Arabic, avares://KotH.Game/Menu/Fonts#Noto Sans Devanagari, avares://KotH.Game/Menu/Fonts#Noto Sans Thai, fonts:KotHCjk#Noto Sans CJK JP");
        static readonly RenderOptions SmoothText = new RenderOptions
        {
            TextRenderingMode = TextRenderingMode.Antialias,
            EdgeMode = EdgeMode.Antialias,
            BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
        };
        static readonly bool ShotTalk = Environment.GetEnvironmentVariable("KOTH_SHOT_TALK") == "1";

        SimulationBody body;
        TextBlock sdfText;
        ImageElement labelImage, speaker;
        SpriteFromTexture labelSprite;
        const int Slots = 4;
        Texture labelTexture;
        readonly Texture[] staging = new Texture[Slots];
        readonly Queue<(long Frame, Texture Tex)> retired = new Queue<(long, Texture)>();
        byte[] pixelBuffer;
        string shownText, shownName;
        float shown = float.NaN;
        Color shownTint;
        int shownLang = -1;

        public DamageLabel()
        {
            // After the camera, which moves at 1000: the text faces the
            // position the frame is drawn from.
            Priority = 1100;
        }

        /// <summary>What the label shows right now.</summary>
        [DataMemberIgnore] public string Text => shownText;

        // The label (SDF TextBlock or fallback ImageElement) sits between a hidden spacer and the speaker,
        // so it stays centred whether the speaker shows or not.
        StackPanel Row()
        {
            float size = FontSize * PixelsPerMeter * 1.15f, gap = FontSize * PixelsPerMeter * 0.35f;
            var sprite = new SpriteFromTexture { Texture = SpeakerIcon.Of(GraphicsDevice) };
            speaker = new ImageElement
            {
                Source = sprite, Width = size, Height = size, Margin = new Thickness(gap, 0f, 0f, 0f),
                Visibility = Visibility.Hidden, CanBeHitByUser = false, VerticalAlignment = VerticalAlignment.Center,
            };
            var spacer = new ImageElement { Width = size, Height = size, Margin = new Thickness(0f, 0f, gap, 0f), Visibility = Visibility.Hidden };
            sdfText = new TextBlock
            {
                Font = Font,
                TextSize = FontSize * PixelsPerMeter,
                TextColor = Color,
                TextAlignment = Stride.Graphics.TextAlignment.Center,
                WrapText = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CanBeHitByUser = false,
            };
            labelSprite = new SpriteFromTexture();
            labelImage = new ImageElement
            {
                Source = labelSprite, Width = 1f, Height = 1f,
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                CanBeHitByUser = false,
            };
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Children = { spacer, sdfText, labelImage, speaker },
            };
        }

        public override void Start()
        {
            Entity.Add(new UIComponent
            {
                Page = new UIPage { RootElement = Row() },
                IsFullScreen = false,
                IsBillboard = false,
                Size = new Vector3(RectMeters, 1f),
                Resolution = new Vector3(RectMeters * PixelsPerMeter, 1000f),
                ResolutionStretch = ResolutionStretch.FixedWidthFixedHeight,
            });

            if (Target == null || Motor == null || Font == null)
                Log.Warning("DamageLabel without target or font stays empty.");
        }

        public override void Cancel()
        {
            labelTexture?.Dispose();
            labelTexture = null;
            for (int i = 0; i < Slots; i++)
            {
                staging[i]?.Dispose();
                staging[i] = null;
            }
            while (retired.Count > 0)
                retired.Dequeue().Tex.Dispose();
        }

        public override void Update()
        {
            long frame = Game.UpdateTime.FrameCount;
            while (retired.Count > 0 && frame - retired.Peek().Frame >= Slots + 1)
                retired.Dequeue().Tex.Dispose();

            if (Target == null || Motor == null || sdfText == null)
                return;

            // The same formula the visual system uses, rather than reading the drawn capsule.
            Vector3 center = UMath.Lerp(Motor.PreviousBodyCenter, Motor.BodyCenter, TickInterpolation.Alpha);
            Vector3 position = center + Vector3.UnitY * (Motor.Height * 0.5f + Offset);
            Entity.Transform.Position = position;

            if (Camera != null)
                Entity.Transform.Rotation = Facing(Camera.Position - position);

            body ??= Motor.Entity.Get<SimulationBody>();
            // Caught and free ones are gone from the round (1, 2, 3, Free); a catcher's label is red.
            UIComponent ui = Entity.Get<UIComponent>();
            bool inRound = body?.IsOut != true;
            if (ui != null && ui.Enabled != inRound)
                ui.Enabled = inRound;
            Color tint = body?.Role == FreeRole.Catcher ? CatcherColor
                       : Self != null && body != Self && HillMatch.SameTeam(body, Self) ? MateColor : Color;

            bool speaking = ShotTalk || NetId != 0 && VoiceChat.IsSpeaking(NetId);
            if (speaking != (speaker.Visibility == Visibility.Visible))
                speaker.Visibility = speaking ? Visibility.Visible : Visibility.Hidden;

            // Redrawn only when value, name, tint or language changed.
            float value = Target.Percent;
            if (value != shown || tint != shownTint || Name != shownName || shownLang != Loc.Version)
            {
                shown = value;
                shownTint = tint;
                shownName = Name;
                shownLang = Loc.Version;
                UpdateVisual(Name, value.ToString("F1", Loc.Numbers) + " %", tint);
            }
        }

        void UpdateVisual(string name, string percent, Color tint)
        {
            string full = !string.IsNullOrEmpty(name) ? name + "  " + percent : percent;
            shownText = full;
            if (Font != null && AllInFont(Font, full))
            {
                sdfText.Text = full;
                sdfText.TextColor = tint;
                sdfText.Visibility = Visibility.Visible;
                labelImage.Visibility = Visibility.Collapsed;
                return;
            }
            sdfText.Visibility = Visibility.Collapsed;
            labelImage.Visibility = Visibility.Visible;
            RenderBitmap(name, Loc.Ltr(percent), tint);
        }

        static bool AllInFont(SpriteFont font, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!char.IsWhiteSpace(c) && !font.IsCharPresent(c))
                    return false;
            }
            return true;
        }

        void RenderBitmap(string name, string percent, Color tint)
        {
            MenuOverlay.Start<MenuApp>();
            double pxSize = FontSize * PixelsPerMeter * Supersample;
            var typeface = new Typeface(LabelFont, FontStyle.Normal, FontWeight.Normal);
            var brush = new SolidColorBrush(AvaloniaColor.FromArgb(tint.A, tint.R, tint.G, tint.B));
            double gap = pxSize * 0.45;

            using var pctLayout = MakeLayout(percent, typeface, pxSize, brush, 640);
            using TextLayout nameLayout = !string.IsNullOrEmpty(name) ? MakeLayout(name, typeface, pxSize, brush, MaxNamePx) : null;

            double contentW = nameLayout != null ? nameLayout.WidthIncludingTrailingWhitespace + gap + pctLayout.WidthIncludingTrailingWhitespace
                                                 : pctLayout.WidthIncludingTrailingWhitespace;
            double contentH = Math.Max(nameLayout?.Height ?? 0, pctLayout.Height);
            int texW = Math.Clamp((int)Math.Ceiling(contentW) + Pad * 2, 16, MaxTexWidth);
            int texH = Math.Clamp((int)Math.Ceiling(contentH) + Pad * 2, 16, MaxTexHeight);

            using var rtb = new RenderTargetBitmap(new PixelSize(texW, texH), new Vector(96, 96));
            using (DrawingContext dc = rtb.CreateDrawingContext())
            using (dc.PushRenderOptions(SmoothText))
            {
                double x = (texW - contentW) * 0.5;
                if (nameLayout != null)
                {
                    nameLayout.Draw(dc, new Avalonia.Point(x, (texH - nameLayout.Height) * 0.5));
                    x += nameLayout.WidthIncludingTrailingWhitespace + gap;
                }
                pctLayout.Draw(dc, new Avalonia.Point(x, (texH - pctLayout.Height) * 0.5));
            }

            int stride = texW * 4, bytes = stride * texH;
            if (pixelBuffer == null || pixelBuffer.Length != bytes)
                pixelBuffer = new byte[bytes];
            unsafe
            {
                fixed (byte* p = pixelBuffer)
                    rtb.CopyPixels(new PixelRect(0, 0, texW, texH), (IntPtr)p, bytes, stride);
            }
            UploadTexture(texW, texH);
            labelImage.Width = (float)texW / Supersample;
            labelImage.Height = (float)texH / Supersample;
        }

        // Uses a 4-slot staging ring (same pattern as MenuOverlay) so Vulkan never maps a Default/Immutable
        // texture or destroys one while a previous frame's command buffer still reads it.
        unsafe void UploadTexture(int texW, int texH)
        {
            CommandList cmd = Game.GraphicsContext.CommandList;
            long frame = Game.UpdateTime.FrameCount;
            if (labelTexture == null || labelTexture.Width != texW || labelTexture.Height != texH)
            {
                if (labelTexture != null)
                    retired.Enqueue((frame, labelTexture));
                labelTexture = Texture.New2D(GraphicsDevice, texW, texH, Stride.Graphics.PixelFormat.B8G8R8A8_UNorm_SRgb, TextureFlags.ShaderResource);
                for (int i = 0; i < Slots; i++)
                {
                    if (staging[i] != null)
                        retired.Enqueue((frame, staging[i]));
                    staging[i] = Texture.New2D(GraphicsDevice, texW, texH, Stride.Graphics.PixelFormat.B8G8R8A8_UNorm_SRgb,
                                               TextureFlags.None, 1, GraphicsResourceUsage.Staging);
                }
                labelSprite.Texture = labelTexture;
            }
            int slot = (int)(frame % Slots), rowBytes = texW * 4;
            MappedResource mapped = cmd.MapSubResource(staging[slot], 0, MapMode.Write);
            try
            {
                int pitch = mapped.DataBox.RowPitch;
                fixed (byte* src = pixelBuffer)
                    for (int y = 0; y < texH; y++)
                        System.Buffer.MemoryCopy(src + y * rowBytes, (byte*)mapped.DataBox.DataPointer + y * pitch, rowBytes, rowBytes);
            }
            finally
            {
                cmd.UnmapSubResource(mapped);
            }
            cmd.Copy(staging[slot], labelTexture);
        }

        static TextLayout MakeLayout(string text, Typeface typeface, double size, IBrush brush, double maxWidth)
            => new TextLayout(text, typeface, size, brush, AvaloniaTextAlignment.Left, TextWrapping.NoWrap,
                              TextTrimming.CharacterEllipsis, maxWidth: maxWidth, maxLines: 1);

        /// <summary>Width and height of s in metres, as this label would draw it.</summary>
        public Vector2 Measure(string s)
        {
            if (string.IsNullOrEmpty(s))
                return Vector2.Zero;
            if (Font != null && AllInFont(Font, s))
                return Font.MeasureString(s) * (FontSize / Font.Size);
            MenuOverlay.Start<MenuApp>();
            double pxSize = FontSize * PixelsPerMeter * Supersample;
            var typeface = new Typeface(LabelFont, FontStyle.Normal, FontWeight.Normal);
            using var layout = MakeLayout(s, typeface, pxSize, Brushes.White, MaxNamePx);
            return new Vector2((float)(layout.WidthIncludingTrailingWhitespace / (PixelsPerMeter * Supersample)),
                               (float)(layout.Height / (PixelsPerMeter * Supersample)));
        }

        /// <summary>Turns the UI plane, which faces +Z, to look at the camera: Unity's
        /// LookRotation(position - camera) with the world's up.</summary>
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
