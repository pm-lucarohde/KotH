#!/usr/bin/env -S dotnet run
// One link of the duo chain (Luca: a metal chain, 3.5 m): a round wire bent into a stadium, its long
// axis along +Z, lying in the XZ plane, inner length = the chain's pitch (32 links make 3.5 m).
// Writes KotH.Game/Resources/Models/ChainLink.glb (Models/ChainLink in Game Studio). Run from the repo.
#:include ../Common/Glb.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using KotH.Tools;

System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

const float Wire = 0.011f;            // wire radius
const float Half = 0.025f;            // half the centreline's width
const float Pitch = 3.5f / 32f;       // inner length
float straight = (Pitch + 2f * Wire - 2f * Half) * 0.5f;   // half the straight part
const int Around = 36, Ring = 8;

var positions = new List<float>();
var normals = new List<float>();
var indices = new List<ushort>();

// The centreline: right straight (+X) up +Z, the far bend, left straight down, the near bend.
(float X, float Z, float TX, float TZ) Centre(float u)
{
    float bend = MathF.PI * Half, line = 2f * straight, total = 2f * (bend + line), s = u * total;
    if (s < line)
        return (Half, -straight + s, 0f, 1f);
    s -= line;
    if (s < bend)
    {
        float a = s / Half;
        return (Half * MathF.Cos(a), straight + Half * MathF.Sin(a), -MathF.Sin(a), MathF.Cos(a));
    }
    s -= bend;
    if (s < line)
        return (-Half, straight - s, 0f, -1f);
    s -= line;
    float b = s / Half;
    return (-Half * MathF.Cos(b), -straight - Half * MathF.Sin(b), MathF.Sin(b), -MathF.Cos(b));
}

for (int i = 0; i < Around; i++)
{
    var (cx, cz, tx, tz) = Centre(i / (float)Around);
    // Across the wire: outward in the plane (tangent turned right) and up.
    float ox = tz, oz = -tx;
    for (int j = 0; j < Ring; j++)
    {
        float a = j * 2f * MathF.PI / Ring;
        float nx = ox * MathF.Cos(a), ny = MathF.Sin(a), nz = oz * MathF.Cos(a);
        positions.AddRange(new[] { cx + nx * Wire, ny * Wire, cz + nz * Wire });
        normals.AddRange(new[] { nx, ny, nz });
    }
}
for (int i = 0; i < Around; i++)
    for (int j = 0; j < Ring; j++)
    {
        int a = i * Ring + j, b = (i + 1) % Around * Ring + j, c = (i + 1) % Around * Ring + (j + 1) % Ring, d = i * Ring + (j + 1) % Ring;
        indices.AddRange(new[] { (ushort)a, (ushort)d, (ushort)b, (ushort)b, (ushort)d, (ushort)c });
    }

byte[] pos = MemoryMarshal.AsBytes(positions.ToArray().AsSpan()).ToArray();
byte[] nor = MemoryMarshal.AsBytes(normals.ToArray().AsSpan()).ToArray();
byte[] idx = MemoryMarshal.AsBytes(indices.ToArray().AsSpan()).ToArray();
var bin = new byte[pos.Length + nor.Length + idx.Length];
pos.CopyTo(bin, 0);
nor.CopyTo(bin, pos.Length);
idx.CopyTo(bin, pos.Length + nor.Length);
float outX = Half + Wire, outZ = straight + Half + Wire;
int count = positions.Count / 3;
string json = $$$"""
{"asset":{"version":"2.0","generator":"Tools/figure/ChainLink.cs"},"scene":0,"scenes":[{"nodes":[0]}],
"nodes":[{"name":"ChainLink","mesh":0}],
"materials":[{"name":"ChainLink","pbrMetallicRoughness":{"baseColorFactor":[0.5,0.51,0.53,1],"metallicFactor":1,"roughnessFactor":0.35}}],
"meshes":[{"name":"ChainLink","primitives":[{"attributes":{"POSITION":0,"NORMAL":1},"indices":2,"material":0}]}],
"buffers":[{"byteLength":{{{bin.Length}}}}],
"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":{{{pos.Length}}},"target":34962},
{"buffer":0,"byteOffset":{{{pos.Length}}},"byteLength":{{{nor.Length}}},"target":34962},
{"buffer":0,"byteOffset":{{{pos.Length + nor.Length}}},"byteLength":{{{idx.Length}}},"target":34963}],
"accessors":[{"bufferView":0,"componentType":5126,"count":{{{count}}},"type":"VEC3","min":[{{{-outX}}},{{{-Wire}}},{{{-outZ}}}],"max":[{{{outX}}},{{{Wire}}},{{{outZ}}}]},
{"bufferView":1,"componentType":5126,"count":{{{count}}},"type":"VEC3"},
{"bufferView":2,"componentType":5123,"count":{{{indices.Count}}},"type":"SCALAR"}]}
""";
string path = "KotH.Game/Resources/Models/ChainLink.glb";
Glb.Write(path, json.Replace("\n", ""), bin);
Console.WriteLine($"{path}: {count} vertices, {indices.Count / 3} triangles, {2f * outZ * 100f:F1} x {2f * outX * 100f:F1} cm");
