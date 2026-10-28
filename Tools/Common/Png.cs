#nullable disable
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace KotH.Tools;

/// <summary>
/// 8-bit PNG without interlacing, as the tools write and read them: grey (L),
/// grey with alpha (LA), RGB and RGBA; palette images are read as RGB or RGBA.
/// Pixels are row-major with <see cref="Channels"/> bytes each.
/// </summary>
public sealed class Png
{
    public int Width, Height, Channels;
    public byte[] Pixels;

    public Png(int width, int height, int channels, byte[] pixels = null)
    {
        Width = width;
        Height = height;
        Channels = channels;
        Pixels = pixels ?? new byte[width * height * channels];
    }

    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static Png Read(string path) => Decode(File.ReadAllBytes(path), path);

    /// <summary>A PNG from its bytes (an image inside a glb); name only labels errors.</summary>
    public static Png Decode(byte[] file, string path = "PNG")
    {
        if (!file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException($"{path}: kein PNG");
        int width = 0, height = 0, depth = 0, type = 0;
        byte[] palette = null, alpha = null;
        var data = new MemoryStream();
        for (int at = 8; at < file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
            string kind = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            var body = file.AsSpan(at + 8, length);
            switch (kind)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8];
                    type = body[9];
                    if (depth != 8 || body[12] != 0)
                        throw new NotSupportedException($"{path}: nur 8 Bit ohne Interlacing");
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": alpha = body.ToArray(); break;
                case "IDAT": data.Write(body); break;
            }
            at += 12 + length;
        }
        int channels = type switch { 0 => 1, 4 => 2, 2 => 3, 6 => 4, 3 => 1, _ => throw new NotSupportedException($"{path}: Farbtyp {type}") };
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
                var row = raw.AsSpan(y * stride, stride);
                Unfilter(line[0], line.AsSpan(1), prior, row, channels);
                row.CopyTo(prior);
            }
        }
        if (type != 3)
            return new Png(width, height, channels, raw);
        // Palette: out as RGB, or RGBA when some entry is transparent.
        int outChannels = alpha != null ? 4 : 3;
        var png = new Png(width, height, outChannels);
        for (int i = 0; i < width * height; i++)
        {
            int p = raw[i];
            for (int c = 0; c < 3; c++)
                png.Pixels[i * outChannels + c] = palette[3 * p + c];
            if (alpha != null)
                png.Pixels[i * 4 + 3] = p < alpha.Length ? alpha[p] : (byte)255;
        }
        return png;
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
                _ => throw new InvalidDataException($"PNG-Filter {filter}"),
            };
            row[i] = (byte)(line[i] + predicted);
        }
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public void Write(string path) => File.WriteAllBytes(path, Encode());

    /// <summary>The PNG file's bytes.</summary>
    public byte[] Encode()
    {
        int type = Channels switch { 1 => 0, 2 => 4, 3 => 2, 4 => 6, _ => throw new NotSupportedException() };
        int stride = Width * Channels;
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            // Per row the filter with the smallest sum of magnitudes, as libpng picks it.
            var best = new byte[stride + 1];
            var trial = new byte[stride + 1];
            for (int y = 0; y < Height; y++)
            {
                var row = Pixels.AsSpan(y * stride, stride);
                var prior = y > 0 ? Pixels.AsSpan((y - 1) * stride, stride) : new byte[stride];
                long bestSum = long.MaxValue;
                for (int f = 0; f < 5; f++)
                {
                    trial[0] = (byte)f;
                    long sum = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= Channels ? row[i - Channels] : 0, b = prior[i], c = i >= Channels ? prior[i - Channels] : 0;
                        int predicted = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                        byte d = (byte)(row[i] - predicted);
                        trial[i + 1] = d;
                        sum += d < 128 ? d : 256 - d;
                    }
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        (best, trial) = (trial, best);
                    }
                }
                z.Write(best);
            }
        }
        var file = new MemoryStream();
        file.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8;
        header[9] = (byte)type;
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        Chunk(file, "IEND", []);
        return file.ToArray();
    }

    static void Chunk(Stream s, string kind, ReadOnlySpan<byte> body)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, body.Length);
        s.Write(word);
        var head = System.Text.Encoding.ASCII.GetBytes(kind);
        s.Write(head);
        s.Write(body);
        uint crc = Crc(Crc(0xFFFFFFFFu, head), body) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        s.Write(word);
    }

    static uint[] crcTable;

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        if (crcTable == null)
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            crcTable = table;
        }
        foreach (byte b in data)
            crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
