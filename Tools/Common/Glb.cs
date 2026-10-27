using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace KotH.Tools;

/// <summary>A binary glTF: its JSON and its one binary chunk, with the accessors read out.</summary>
public sealed class Glb
{
    public JsonObject Json;
    public byte[] Bin;

    public static Glb Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != 0x46546C67)
            throw new InvalidDataException($"{path}: kein glb");
        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        var glb = new Glb { Json = JsonNode.Parse(Encoding.UTF8.GetString(file, 20, jsonLength)).AsObject() };
        int at = 20 + jsonLength;
        if (at + 8 <= file.Length)
        {
            int binLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
            glb.Bin = file.AsSpan(at + 8, binLength).ToArray();
        }
        return glb;
    }

    /// <summary>The bytes of a buffer view.</summary>
    public ReadOnlySpan<byte> View(int index)
    {
        var view = Json["bufferViews"]![index]!;
        return Bin.AsSpan((int?)view["byteOffset"] ?? 0, (int)view["byteLength"]);
    }

    /// <summary>An accessor's elements, tightly packed (no stride), as raw bytes.</summary>
    public ReadOnlySpan<byte> AccessorBytes(int index)
    {
        var a = Json["accessors"]![index]!;
        int size = Components((string)a["type"]) * ComponentSize((int)a["componentType"]);
        int count = (int)a["count"];
        var view = Json["bufferViews"]![(int)a["bufferView"]]!;
        int stride = (int?)view["byteStride"] ?? size;
        var bytes = View((int)a["bufferView"])[((int?)a["byteOffset"] ?? 0)..];
        if (stride == size)
            return bytes[..(size * count)];
        var packed = new byte[size * count];
        for (int i = 0; i < count; i++)
            bytes.Slice(i * stride, size).CopyTo(packed.AsSpan(i * size));
        return packed;
    }

    public float[] Floats(int accessor) => MemoryMarshal.Cast<byte, float>(AccessorBytes(accessor)).ToArray();

    /// <summary>An accessor of unsigned integers (indices, joints) as ints.</summary>
    public int[] Ints(int accessor)
    {
        var bytes = AccessorBytes(accessor);
        return (int)Json["accessors"]![accessor]!["componentType"] switch
        {
            5121 => Array.ConvertAll(bytes.ToArray(), b => (int)b),
            5123 => Array.ConvertAll(MemoryMarshal.Cast<byte, ushort>(bytes).ToArray(), v => (int)v),
            _ => MemoryMarshal.Cast<byte, int>(bytes).ToArray(),
        };
    }

    public static int Components(string type) => type switch
    {
        "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16,
        _ => throw new InvalidDataException(type),
    };

    public static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4,
    };

    /// <summary>Writes JSON and binary chunk, each padded to four bytes (JSON with spaces, binary with zeros).</summary>
    public static void Write(string path, string json, ReadOnlySpan<byte> bin)
    {
        byte[] text = Encoding.UTF8.GetBytes(json);
        int jsonPad = -text.Length & 3, binPad = -bin.Length & 3;
        using var file = File.Create(path);
        var word = new byte[4];
        void Put(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(word, v); file.Write(word); }
        Put(0x46546C67);
        Put(2);
        Put((uint)(12 + 8 + text.Length + jsonPad + 8 + bin.Length + binPad));
        Put((uint)(text.Length + jsonPad));
        Put(0x4E4F534A);
        file.Write(text);
        for (int i = 0; i < jsonPad; i++)
            file.WriteByte(0x20);
        Put((uint)(bin.Length + binPad));
        Put(0x004E4942);
        file.Write(bin);
        for (int i = 0; i < binPad; i++)
            file.WriteByte(0);
    }
}
