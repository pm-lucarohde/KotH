#!/usr/bin/env -S dotnet run
// A soft five-pointed star for the sparkle trail's star shape: white, a bright core and a faint halo,
// alpha only (the particles tint and fade it), 128 px, 4× supersampled, to
// KotH.Game/Resources/Effects/Star.png.     dotnet run Tools/effects/Star.cs
#:include ../Common/Png.cs
using System;
using System.IO;
using KotH.Tools;

const int Out = 128;
const double Outer = 0.78, Inner = 0.36;   // tip and notch radius, shares of the half size
const double Soft = 0.05, Halo = 0.25;     // edge blur and the glow round it

var png = new Png(Out, Out, 4);
for (int y = 0; y < Out; y++)
    for (int x = 0; x < Out; x++)
    {
        double a = 0;
        for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
            {
                double u = (x + (sx + 0.5) / 4) / Out * 2 - 1, v = (y + (sy + 0.5) / 4) / Out * 2 - 1;
                double r = Math.Sqrt(u * u + v * v), angle = Math.Atan2(u, v);
                // The star's edge at this angle: straight lines between tips and notches.
                double sector = Math.PI / 5, t = Math.Abs(((angle % (2 * sector)) + 2 * sector) % (2 * sector) - sector) / sector;
                double edge = 1 / ((1 - t) / Outer + t / Inner);
                double star = Math.Clamp((edge - r) / Soft + 0.5, 0, 1);
                double glow = Halo * Math.Exp(-r * r * 6) * (r < 1 ? 1 : 0);
                a += Math.Max(star, glow);
            }
        int o = 4 * (y * Out + x);
        png.Pixels[o] = png.Pixels[o + 1] = png.Pixels[o + 2] = 255;
        png.Pixels[o + 3] = (byte)Math.Round(Math.Clamp(a / 16, 0, 1) * 255);
    }
string path = Path.GetFullPath(Path.Combine(Here(), "../../KotH.Game/Resources/Effects/Star.png"));
png.Write(path);
Console.WriteLine($"{path}: {Out} px");
return 0;

static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
