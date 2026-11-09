using System;
using Stride.Graphics;

namespace KotH.Client.Hud
{
    /// <summary>The white speaker with two waves (ChatView's sign, the same 24-unit drawing) as a texture for
    /// Stride's UI, drawn once per device with 4 × 4 samples a pixel. Premultiplied, as the UI blends.</summary>
    public static class SpeakerIcon
    {
        public const int Pixels = 64;
        const int Samples = 4;
        const float Units = 24f;

        static Texture texture;
        static GraphicsDevice drawnFor;

        public static Texture Of(GraphicsDevice device)
        {
            if (texture == null || drawnFor != device)
            {
                texture = Draw(device);
                drawnFor = device;
            }
            return texture;
        }

        static Texture Draw(GraphicsDevice device)
        {
            var data = new byte[Pixels * Pixels * 4];
            float step = Units / Pixels / Samples;
            for (int y = 0; y < Pixels; y++)
                for (int x = 0; x < Pixels; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < Samples; sy++)
                        for (int sx = 0; sx < Samples; sx++)
                            if (Inside((x * Samples + sx + 0.5f) * step, (y * Samples + sy + 0.5f) * step))
                                hits++;
                    byte v = (byte)MathF.Round(255f * hits / (Samples * Samples));
                    int i = (y * Pixels + x) * 4;
                    data[i] = data[i + 1] = data[i + 2] = data[i + 3] = v;
                }
            return Texture.New2D(device, Pixels, Pixels, PixelFormat.R8G8B8A8_UNorm, data);
        }

        // The body M3,9 L7,9 L12,4 L12,20 L7,15 L3,15, and the arcs through (15.5, 8.5 to 15.5) and (18.5, 5.5 to 18.5).
        static readonly float[] Body = { 3, 9, 7, 9, 12, 4, 12, 20, 7, 15, 3, 15 };

        static bool Inside(float x, float y)
            => InBody(x, y) || OnArc(x, y, 11.93f, 5f, 15.5f, 8.5f) || OnArc(x, y, 12.28f, 9f, 18.5f, 5.5f);

        static bool InBody(float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = Body.Length - 2; i < Body.Length; j = i, i += 2)
                if (Body[i + 1] > y != Body[j + 1] > y
                    && x < (Body[j] - Body[i]) * (y - Body[i + 1]) / (Body[j + 1] - Body[i + 1]) + Body[i])
                    inside = !inside;
            return inside;
        }

        // A wave 2 units thick round (cx, 12), from its top end (ex, ey) down to its mirror, with round caps.
        static bool OnArc(float x, float y, float cx, float r, float ex, float ey)
        {
            float dx = x - cx, dy = y - 12f, d = MathF.Sqrt(dx * dx + dy * dy);
            if (dx > 0f && MathF.Abs(dy) <= 12f - ey && MathF.Abs(d - r) <= 1f)
                return true;
            float ux = x - ex, top = y - ey, bottom = y - (24f - ey);
            return ux * ux + top * top <= 1f || ux * ux + bottom * bottom <= 1f;
        }
    }
}
