#!/usr/bin/env -S dotnet run
// The dash's ground shards: a hexagon cut from each ground texture (grass, sand, snow, rock, and
// the Test map's grey ground tinted from rock), soft-edged, its rim a little darker like a broken
// chip, 128 px with alpha, to KotH.Game/Resources/Effects/Shards/NAMEChip.png.
//   dotnet run Tools/effects/HexChips.cs
#:include ../Common/Png.cs
using System;
using System.IO;
using KotH.Tools;

const int Out = 128;                 // chip texture size
const int Cut = 256;                 // source pixels cut out: half a metre at 512 px/m
const double Rim = 0.16;             // share of the radius that darkens towards the edge
const double RimDark = 0.25;         // how much darker the very edge is
// The Test map's Ground.sdmat colour, linear albedo shown as sRGB bytes.
(double R, double G, double B) groundTint = (0.38, 0.40, 0.42);

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
string src = Path.Combine(root, "KotH.Game/Resources/Textures/Terrain");
string outDir = Path.Combine(root, "KotH.Game/Resources/Effects/Shards");
Directory.CreateDirectory(outDir);

foreach (string name in new[] { "Grass", "Sand", "Snow", "Rock" })
    Chip(Png.Read(Path.Combine(src, name + ".png")), null).Write(Path.Combine(outDir, name + "Chip.png"));
Chip(Png.Read(Path.Combine(src, "Rock.png")), groundTint).Write(Path.Combine(outDir, "GroundChip.png"));
Console.WriteLine($"5 chips, {Out} px, to {outDir}");
return 0;

// A box-filtered cut from the texture's middle, masked to a pointy-top hexagon.
static Png Chip(Png tex, (double R, double G, double B)? tint)
{
    var chip = new Png(Out, Out, 4);
    int x0 = (tex.Width - Cut) / 2, y0 = (tex.Height - Cut) / 2, f = Cut / Out;
    for (int y = 0; y < Out; y++)
        for (int x = 0; x < Out; x++)
        {
            double r = 0, g = 0, b = 0;
            for (int dy = 0; dy < f; dy++)
                for (int dx = 0; dx < f; dx++)
                {
                    int i = ((y0 + y * f + dy) * tex.Width + x0 + x * f + dx) * tex.Channels;
                    r += tex.Pixels[i];
                    g += tex.Pixels[i + 1];
                    b += tex.Pixels[i + 2];
                }
            double n = f * f;
            r /= n; g /= n; b /= n;
            if (tint is { } t)
            {
                // The rock's light and dark, coloured as the plain ground.
                double l = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0 / 0.5;
                r = Math.Min(255, t.R * l * 255 * 1.4);
                g = Math.Min(255, t.G * l * 255 * 1.4);
                b = Math.Min(255, t.B * l * 255 * 1.4);
            }

            // Distance to the hexagon's edge as a share of its inner radius, 1 at the edge.
            double px = (x + 0.5) / Out * 2 - 1, py = (y + 0.5) / Out * 2 - 1;
            double inner = 0.92;
            double d = Math.Max(Math.Abs(px), Math.Abs(px) * 0.5 + Math.Abs(py) * 0.8660254) / inner;
            double alpha = Math.Clamp((1 - d) * Out * inner / 2 / 1.5, 0, 1);
            double shade = 1 - RimDark * Math.Clamp((d - (1 - Rim)) / Rim, 0, 1);

            int o = (y * Out + x) * 4;
            chip.Pixels[o] = (byte)Math.Round(r * shade);
            chip.Pixels[o + 1] = (byte)Math.Round(g * shade);
            chip.Pixels[o + 2] = (byte)Math.Round(b * shade);
            chip.Pixels[o + 3] = (byte)Math.Round(alpha * 255);
        }
    return chip;
}

static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
