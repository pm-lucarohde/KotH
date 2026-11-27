#!/usr/bin/env -S dotnet run
// A directional water wake for wading: a curved bow wave at the front (v = 0, local +Z) whose
// two arms sweep back in a V behind the legs (v = 1, local -Z), alpha only (particles tint and
// fade it), 128 px, 4x supersampled, to KotH.Game/Resources/Effects/Ripple.png.
//   dotnet run Tools/effects/Ripple.cs
#:include ../Common/Png.cs
using System;
using System.IO;
using KotH.Tools;

const int Out = 128;
const double Nose = 0.58, Curve = 1.35;   // broad parabolic bow arc curving smoothly backward
const double Width = 0.052;

static double Smooth(double edge0, double edge1, double x)
{
    double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

var png = new Png(Out, Out, 4);
for (int y = 0; y < Out; y++)
    for (int x = 0; x < Out; x++)
    {
        double a = 0;
        for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
            {
                // u in [-1, 1] across; w in [-1, 1] forward (+1 at y = 0 / local +Z, -1 behind).
                double u = (x + (sx + 0.5) / 4) / Out * 2 - 1;
                double w = 1 - (y + (sy + 0.5) / 4) / Out * 2;

                // Distance to the curved bow wave w = Nose - Curve * u^2.
                double curve = Nose - Curve * u * u;
                double slopeAt = -2.0 * Curve * u;
                double dist = (w - curve) / Math.Sqrt(1.0 + slopeAt * slopeAt);

                // Main curved water crest: crisp front, gentle trailing wash behind it.
                double d1 = dist / Width;
                double main = Math.Exp(-d1 * d1 * (d1 > 0 ? 3.0 : 0.65));

                // Delicate second concentric wake arc trailing behind the main bow wave.
                double d2 = (dist + 0.21) / (Width * 1.15);
                double second = 0.38 * Math.Exp(-d2 * d2 * (d2 > 0 ? 2.0 : 0.75));

                // Softly taper the trailing tips and keep clear of the quad borders.
                double edgeX = Smooth(0.96, 0.52, Math.Abs(u));
                double edgeBack = Smooth(-0.92, -0.30, w);
                double edgeFront = Smooth(0.96, 0.72, w);
                double innerFade = Smooth(0.86, 0.35, Math.Abs(u));
                double wave = Math.Max(main, second * innerFade) * edgeX * edgeBack * edgeFront;
                a += wave;
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
