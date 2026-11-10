using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Stride.Graphics;

namespace KotH.Client.CustomMaps
{
    /// <summary>A custom map's PNG textures as GPU textures: decoded here (8 bit, any colour type,
    /// no interlacing, as Tools/Common/Png.cs), widened to RGBA, a box-filtered mip chain, sRGB for
    /// colour. At most 4096² each; anything else is an InvalidDataException.</summary>
    public static class MapTextures
    {
        public const int MaxSize = 4096;
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static Texture Create(GraphicsDevice device, byte[] png, bool srgb)
        {
            byte[] rgba = Decode(png, out int width, out int height);
            int levels = 1;
            for (int s = Math.Max(width, height); s > 1; s >>= 1)
                levels++;
            var mips = new byte[levels][];
            mips[0] = rgba;
            int w = width, h = height;
            for (int l = 1; l < levels; l++)
            {
                mips[l] = Half(mips[l - 1], w, h, out w, out h);
            }
            var handles = new GCHandle[levels];
            var boxes = new DataBox[levels];
            try
            {
                w = width;
                h = height;
                for (int l = 0; l < levels; l++)
                {
                    handles[l] = GCHandle.Alloc(mips[l], GCHandleType.Pinned);
                    boxes[l] = new DataBox(handles[l].AddrOfPinnedObject(), w * 4, w * h * 4);
                    w = Math.Max(1, w / 2);
                    h = Math.Max(1, h / 2);
                }
                return Texture.New2D(device, width, height, levels,
                                     srgb ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm, boxes);
            }
            finally
            {
                foreach (GCHandle handle in handles)
                    if (handle.IsAllocated)
                        handle.Free();
            }
        }

        /// <summary>A 1×1 texture of one colour (no texture given: the tint alone shows).</summary>
        public static Texture Plain(GraphicsDevice device, byte r, byte g, byte b, byte a)
            => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { r, g, b, a });

        static byte[] Half(byte[] src, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w / 2);
            nh = Math.Max(1, h / 2);
            var dst = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                        int y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                        int sum = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c]
                                + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                        dst[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
            return dst;
        }

        /// <summary>RGBA8 pixels of an 8-bit PNG.</summary>
        public static byte[] Decode(byte[] file, out int width, out int height)
        {
            if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
                throw new InvalidDataException("texture is not a PNG");
            width = height = 0;
            int type = 0;
            byte[] palette = null, alpha = null;
            var data = new MemoryStream();
            for (int at = 8; at + 8 <= file.Length;)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
                if (length < 0 || at + 12 + length > file.Length)
                    throw new InvalidDataException("PNG chunk out of range");
                ReadOnlySpan<byte> body = file.AsSpan(at + 8, length);
                switch (System.Text.Encoding.ASCII.GetString(file, at + 4, 4))
                {
                    case "IHDR":
                        width = BinaryPrimitives.ReadInt32BigEndian(body);
                        height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                        type = body[9];
                        if (body[8] != 8 || body[12] != 0)
                            throw new InvalidDataException("PNG: only 8 bit without interlacing");
                        break;
                    case "PLTE": palette = body.ToArray(); break;
                    case "tRNS": alpha = body.ToArray(); break;
                    case "IDAT": data.Write(body); break;
                }
                at += 12 + length;
            }
            if (width <= 0 || height <= 0 || width > MaxSize || height > MaxSize)
                throw new InvalidDataException($"PNG {width}×{height}: 1 to {MaxSize} pixels a side");
            int channels = type switch { 0 => 1, 4 => 2, 2 => 3, 6 => 4, 3 => 1, _ => throw new InvalidDataException($"PNG colour type {type}") };
            if (type == 3 && palette == null)
                throw new InvalidDataException("PNG palette missing");
            int stride = width * channels;
            var raw = new byte[height * stride];
            data.Position = 0;
            using (var z = new ZLibStream(data, CompressionMode.Decompress))
            {
                var line = new byte[stride + 1];
                var prior = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    z.ReadExactly(line);
                    Span<byte> row = raw.AsSpan(y * stride, stride);
                    Unfilter(line[0], line.AsSpan(1), prior, row, channels);
                    row.CopyTo(prior);
                }
            }
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                byte r, g, b, a = 255;
                switch (type)
                {
                    case 0: r = g = b = raw[i]; break;
                    case 4: r = g = b = raw[2 * i]; a = raw[2 * i + 1]; break;
                    case 2: r = raw[3 * i]; g = raw[3 * i + 1]; b = raw[3 * i + 2]; break;
                    case 6: r = raw[4 * i]; g = raw[4 * i + 1]; b = raw[4 * i + 2]; a = raw[4 * i + 3]; break;
                    default:
                        int p = raw[i];
                        if (3 * p + 2 >= palette.Length)
                            throw new InvalidDataException("PNG palette index out of range");
                        r = palette[3 * p]; g = palette[3 * p + 1]; b = palette[3 * p + 2];
                        a = alpha != null && p < alpha.Length ? alpha[p] : (byte)255;
                        break;
                }
                rgba[4 * i] = r;
                rgba[4 * i + 1] = g;
                rgba[4 * i + 2] = b;
                rgba[4 * i + 3] = a;
            }
            return rgba;
        }

        static void Unfilter(int filter, ReadOnlySpan<byte> line, ReadOnlySpan<byte> prior, Span<byte> row, int bpp)
        {
            for (int i = 0; i < line.Length; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {filter}"),
                };
                row[i] = (byte)(line[i] + predicted);
            }
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
