#!/usr/bin/env -S dotnet run
// Pyramid's pond sounds from CC0 recordings (Tools/audio/water, see its README): three wading steps and a
// splash, decoded by ffmpeg, the silence before the sound cut, as loud as steps.wav over its loudest 400 ms
// (the splash 1.4 times), 48 kHz 16 bit mono, to KotH.Game/Resources/Sounds/.
//   dotnet run Tools/audio/Water.cs        (after Tools/audio/Sfx.cs, which writes steps.wav; needs ffmpeg)
using System;
using System.Diagnostics;
using System.IO;

const int Rate = 48000;
const double Fade = 0.03;            // seconds faded out at the end
string target = Path.GetFullPath(Path.Combine(Here(), "../../KotH.Game/Resources/Sounds"));
string source = Path.Combine(Here(), "water");
double reference = Loudest(Read(Path.Combine(target, "steps.wav")));

string[] wades = ["splash_06", "splash_08", "splash_15"];
for (int k = 0; k < wades.Length; k++)
    Save($"wade{k + 1}", Recording(wades[k]), 1.0);
Save("splash", Recording("splash_04"), 1.4);
return 0;

// A recording as mono samples at Rate, from 5 ms before it first rises over -40 dB of its peak.
double[] Recording(string name)
{
    string raw = Path.Combine(Path.GetTempPath(), name + ".raw");
    using (var ff = Process.Start("ffmpeg", ["-v", "error", "-y", "-i", Path.Combine(source, name + ".ogg"),
                                              "-ac", "1", "-ar", Rate.ToString(), "-f", "s16le", raw])!)
        ff.WaitForExit();
    byte[] b = File.ReadAllBytes(raw);
    File.Delete(raw);
    var s = new double[b.Length / 2];
    double peak = 0;
    for (int i = 0; i < s.Length; i++)
        peak = Math.Max(peak, Math.Abs(s[i] = BitConverter.ToInt16(b, 2 * i) / 32768.0));
    int first = Array.FindIndex(s, v => Math.Abs(v) > peak * 0.01);
    return s[Math.Max(0, first - Rate / 200)..];
}

// Loudness as RMS over the loudest 400 ms.
static double Loudest(double[] s)
{
    int w = (int)(0.4 * Rate);
    double sum = 0, best = 0;
    for (int i = 0; i < s.Length; i++)
    {
        sum += s[i] * s[i];
        if (i >= w)
            sum -= s[i - w] * s[i - w];
        best = Math.Max(best, sum);
    }
    return Math.Sqrt(best / Math.Min(w, s.Length));
}

void Save(string name, double[] s, double louder)
{
    double gain = reference * louder / Loudest(s), peak = 0;
    for (int i = 0; i < s.Length; i++)
    {
        double left = (double)(s.Length - 1 - i) / Rate;
        s[i] *= gain * Math.Min(1, left / Fade);
        peak = Math.Max(peak, Math.Abs(s[i]));
    }
    if (peak > 0.89)
        for (int i = 0; i < s.Length; i++)
            s[i] *= 0.89 / peak;
    using var w = new BinaryWriter(File.Create(Path.Combine(target, name + ".wav")));
    w.Write("RIFF"u8); w.Write(36 + 2 * s.Length); w.Write("WAVEfmt "u8);
    w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(2 * Rate); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8); w.Write(2 * s.Length);
    foreach (double v in s)
        w.Write((short)Math.Round(Math.Clamp(v, -1, 1) * 32767));
    Console.WriteLine($"{name,-7} {(double)s.Length / Rate:F2} s, peak {Math.Min(peak, 0.89):F2}");
}

// A 16-bit mono WAV's samples.
static double[] Read(string path)
{
    using var r = new BinaryReader(File.OpenRead(path));
    r.ReadBytes(12);
    while (true)
    {
        string id = new string(r.ReadChars(4));
        int size = r.ReadInt32();
        if (id != "data")
        {
            r.ReadBytes(size);
            continue;
        }
        var s = new double[size / 2];
        for (int i = 0; i < s.Length; i++)
            s[i] = r.ReadInt16() / 32768.0;
        return s;
    }
}

static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
