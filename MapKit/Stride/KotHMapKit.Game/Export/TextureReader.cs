using System;
using System.IO;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;

namespace KotH.MapKit
{
    /// <summary>A kit texture as RGBA8 (top mip), loaded from the compiled content on the CPU (a GPU
    /// read-back mid-frame never returns under Vulkan): uncompressed formats as they are, the block
    /// formats of Game Studio's "Fast" quality (BC1-BC3) decoded here; others ask for Compress off.</summary>
    public static class TextureReader
    {
        public const int MaxSize = 4096;

        /// <summary>The compiled texture's image: content chunk header, a "streamed" flag (kit textures
        /// are not streamable), then the image in Stride's own format.</summary>
        public static Image Load(ContentManager content, string url)
        {
            using Stream stream = content.OpenAsStream(url, StreamFlags.Seekable);
            var reader = new BinarySerializationReader(stream);
            ChunkHeader header = ChunkHeader.Read(reader);
            if (header == null)
                throw new InvalidDataException("no content header");
            stream.Position = header.OffsetToObject;
            if (reader.ReadBoolean())
                throw new InvalidDataException("streamable; untick Stream in its texture asset");
            return Image.Load(stream);
        }

        public static byte[] Rgba(Image image, out int width, out int height)
        {
            PixelBuffer top = image.PixelBuffer[0];
            width = top.Width;
            height = top.Height;
            PixelFormat format = top.Format;
            // GetPixels counts block formats as empty: the raw buffer instead.
            var data = new byte[top.BufferStride];
            System.Runtime.InteropServices.Marshal.Copy(top.DataPointer, data, 0, data.Length);
            byte[] rgba;
            switch (format)
            {
                case PixelFormat.R8G8B8A8_UNorm:
                case PixelFormat.R8G8B8A8_UNorm_SRgb:
                    rgba = data;
                    break;
                case PixelFormat.B8G8R8A8_UNorm:
                case PixelFormat.B8G8R8A8_UNorm_SRgb:
                case PixelFormat.B8G8R8X8_UNorm:
                case PixelFormat.B8G8R8X8_UNorm_SRgb:
                    rgba = new byte[width * height * 4];
                    bool opaque = format == PixelFormat.B8G8R8X8_UNorm || format == PixelFormat.B8G8R8X8_UNorm_SRgb;
                    for (int i = 0; i < rgba.Length; i += 4)
                    {
                        rgba[i] = data[i + 2];
                        rgba[i + 1] = data[i + 1];
                        rgba[i + 2] = data[i];
                        rgba[i + 3] = opaque ? (byte)255 : data[i + 3];
                    }
                    break;
                case PixelFormat.R8_UNorm:
                    rgba = new byte[width * height * 4];
                    for (int i = 0; i < width * height; i++)
                    {
                        rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = data[i];
                        rgba[4 * i + 3] = 255;
                    }
                    break;
                case PixelFormat.BC1_UNorm:
                case PixelFormat.BC1_UNorm_SRgb:
                    rgba = Blocks(data, width, height, 8, 0);
                    break;
                case PixelFormat.BC2_UNorm:
                case PixelFormat.BC2_UNorm_SRgb:
                    rgba = Blocks(data, width, height, 16, 2);
                    break;
                case PixelFormat.BC3_UNorm:
                case PixelFormat.BC3_UNorm_SRgb:
                    rgba = Blocks(data, width, height, 16, 3);
                    break;
                default:
                    throw new InvalidDataException($"format {format} is not read back; set Compress off in its texture asset");
            }
            while (width > MaxSize || height > MaxSize)
                rgba = Half(rgba, ref width, ref height);
            return rgba;
        }

        // BC1 (alpha 0), BC2 (alpha 2: explicit 4 bit) or BC3 (alpha 3: interpolated), 4x4 blocks.
        static byte[] Blocks(byte[] data, int width, int height, int blockBytes, int alpha)
        {
            var rgba = new byte[width * height * 4];
            int bw = (width + 3) / 4, bh = (height + 3) / 4;
            Span<byte> colors = stackalloc byte[16];
            Span<byte> alphas = stackalloc byte[8];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int at = (by * bw + bx) * blockBytes;
                    int colorAt = at + (alpha == 0 ? 0 : 8);
                    ushort c0 = (ushort)(data[colorAt] | data[colorAt + 1] << 8), c1 = (ushort)(data[colorAt + 2] | data[colorAt + 3] << 8);
                    Unpack(c0, colors.Slice(0, 4));
                    Unpack(c1, colors.Slice(4, 4));
                    bool four = c0 > c1 || alpha != 0;
                    for (int k = 0; k < 3; k++)
                    {
                        colors[8 + k] = (byte)(four ? (2 * colors[k] + colors[4 + k]) / 3 : (colors[k] + colors[4 + k]) / 2);
                        colors[12 + k] = (byte)(four ? (colors[k] + 2 * colors[4 + k]) / 3 : 0);
                    }
                    colors[11] = 255;
                    colors[15] = (byte)(four ? 255 : 0);
                    if (alpha == 3)
                    {
                        alphas[0] = data[at];
                        alphas[1] = data[at + 1];
                        for (int k = 2; k < 8; k++)
                            alphas[k] = (byte)(alphas[0] > alphas[1]
                                ? ((8 - k) * alphas[0] + (k - 1) * alphas[1]) / 7
                                : k < 6 ? ((6 - k) * alphas[0] + (k - 1) * alphas[1]) / 5 : k == 6 ? 0 : 255);
                    }
                    uint bits = (uint)(data[colorAt + 4] | data[colorAt + 5] << 8 | data[colorAt + 6] << 16 | data[colorAt + 7] << 24);
                    ulong alphaBits = 0;
                    if (alpha == 3)
                        for (int k = 0; k < 6; k++)
                            alphaBits |= (ulong)data[at + 2 + k] << (8 * k);
                    for (int p = 0; p < 16; p++)
                    {
                        int x = 4 * bx + (p & 3), y = 4 * by + (p >> 2);
                        if (x >= width || y >= height)
                            continue;
                        int c = (int)(bits >> (2 * p) & 3) * 4, o = (y * width + x) * 4;
                        rgba[o] = colors[c];
                        rgba[o + 1] = colors[c + 1];
                        rgba[o + 2] = colors[c + 2];
                        rgba[o + 3] = alpha == 0 ? colors[c + 3]
                            : alpha == 2 ? (byte)((data[at + p / 2] >> (4 * (p & 1)) & 15) * 17)
                            : alphas[(int)(alphaBits >> (3 * p) & 7)];
                    }
                }
            return rgba;
        }

        static void Unpack(ushort c, Span<byte> to)
        {
            int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31;
            to[0] = (byte)(r << 3 | r >> 2);
            to[1] = (byte)(g << 2 | g >> 4);
            to[2] = (byte)(b << 3 | b >> 2);
            to[3] = 255;
        }

        /// <summary>A box filter to half size (odd edges keep their last row or column).</summary>
        public static byte[] Half(byte[] rgba, ref int width, ref int height)
        {
            int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
            var half = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int k = 0; k < 4; k++)
                    {
                        int x0 = Math.Min(2 * x, width - 1), x1 = Math.Min(2 * x + 1, width - 1);
                        int y0 = Math.Min(2 * y, height - 1), y1 = Math.Min(2 * y + 1, height - 1);
                        int sum = rgba[(y0 * width + x0) * 4 + k] + rgba[(y0 * width + x1) * 4 + k]
                                  + rgba[(y1 * width + x0) * 4 + k] + rgba[(y1 * width + x1) * 4 + k];
                        half[(y * w + x) * 4 + k] = (byte)((sum + 2) / 4);
                    }
            width = w;
            height = h;
            return half;
        }

        /// <summary>RGBA8 pixels as a PNG file (Stride's encoder).</summary>
        public static byte[] Png(byte[] rgba, int width, int height)
        {
            using Image image = Image.New2D(width, height, 1, PixelFormat.R8G8B8A8_UNorm);
            image.PixelBuffer[0].SetPixels(rgba);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFileType.Png);
            return stream.ToArray();
        }
    }
}
