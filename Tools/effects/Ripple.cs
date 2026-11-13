#!/usr/bin/env -S dotnet run
// A ripple on water for the wading rings: a white ring with a soft crest, faint inside and clear
// beyond it, alpha only (the particles tint and fade it), 128 px, 4× supersampled, to
// KotH.Game/Resources/Effects/Ripple.png.     dotnet run Tools/effects/Ripple.cs
#:include ../Common/Png.cs
using System;
using System.IO;
using KotH.Tools;

const int Out = 128;
const double Crest = 0.86, Width = 0.06;   // the ring's radius and half width, shares of the half size
const double Inner = 0.12;                 // the faint wake inside the ring

var png = new Png(Out, Out, 4);
for (int y = 0; y < Out; y++)
    for (int x = 0; x < Out; x++)
    {
        double a = 0;
        for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
            {
                double u = (x + (sx + 0.5) / 4) / Out * 2 - 1, v = (y + (sy + 0.5) / 4) / Out * 2 - 1;
                double r = Math.Sqrt(u * u + v * v), d = (r - Crest) / Width;
                // Steep outside the crest, longer towards the middle, as a wave front.
                double ring = Math.Exp(-d * d * (d > 0 ? 2.0 : 0.5));
                a += Math.Max(ring, r < Crest ? Inner * r / Crest : 0) * (r < 1 ? 1 : 0);
            }
        int o = 4 * (y * Out + x);
        png.Pixels[o] = png.Pixels[o + 1] = png.Pixels[o + 2] = 255;
        png.Pixels[o + 3] = (byte)Math.Round(Math.Clamp(a / 16, 0, 1) * 255);
    }
string path = Path.GetFullPath(Path.Combine(Here(), "../../KotH.Game/Resources/Effects/Ripple.png"));
png.Write(path);
Console.WriteLine($"{path}: {Out} px");
return 0;

static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
