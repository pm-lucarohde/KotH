#!/usr/bin/env -S dotnet run
// Pyramid's pond sounds, synthesised (Sounds/ has no water recording): three wading steps and a
// splash, each a burst of filtered noise for the slosh and rising sine bubbles (Minnaert),
// as loud as steps.wav over its loudest 400 ms, 48 kHz 16 bit mono, to KotH.Game/Resources/Sounds/.
//   dotnet run Tools/audio/Water.cs        (after Tools/audio/Sfx.cs, which writes steps.wav)
using System;
using System.IO;

const int Rate = 48000;
const double Fade = 0.03;            // seconds faded out at the end
string target = Path.GetFullPath(Path.Combine(Here(), "../../KotH.Game/Resources/Sounds"));
double reference = Loudest(Read(Path.Combine(target, "steps.wav")));

for (int k = 1; k <= 3; k++)
    Save($"wade{k}", Wade(new Random(100 + k)), 1.0);
Save("splash", Splash(new Random(200)), 1.4);
return 0;

// A foot into shallow water: a slosh, a smaller one as the water falls back, a few bubbles.
static double[] Wade(Random r)
{
    var s = new double[(int)(0.42 * Rate)];
    Slosh(s, r, 0.0, 1.0, 0.012, 0.085, 350, 2400);
    Slosh(s, r, 0.11 + 0.04 * r.NextDouble(), 0.45, 0.02, 0.06, 500, 3200);
    for (int i = 0, n = 6 + r.Next(5); i < n; i++)
        Bubble(s, r, 0.25 * Math.Pow(r.NextDouble(), 1.5), 500, 1700, 0.2 + 0.35 * r.NextDouble());
    return s;
}

// Landing in the pond: a deep thump, a long wide slosh with a hiss of spray, many bubbles.
static double[] Splash(Random r)
{
    var s = new double[(int)(0.95 * Rate)];
    for (int i = 0; i < (int)(0.2 * Rate); i++)
        s[i] += 0.5 * Math.Sin(2 * Math.PI * 85 * i / Rate) * Math.Exp(-i / (0.045 * Rate));
    Slosh(s, r, 0.0, 1.0, 0.008, 0.16, 250, 4200);
    Slosh(s, r, 0.02, 0.35, 0.03, 0.3, 1800, 9000);
    Slosh(s, r, 0.24, 0.5, 0.04, 0.12, 400, 2600);
    for (int i = 0; i < 26; i++)
        Bubble(s, r, 0.65 * Math.Pow(r.NextDouble(), 1.3), 400, 2000, 0.15 + 0.35 * r.NextDouble());
    return s;
}

// Noise band-passed by one-pole filters, rising over attack and decaying exponentially.
static void Slosh(double[] s, Random r, double at, double amp, double attack, double decay, double low, double high)
{
    double hp = Math.Exp(-2 * Math.PI * low / Rate), lp = 1 - Math.Exp(-2 * Math.PI * high / Rate);
    double x0 = 0, y0 = 0, z = 0;
    int from = (int)(at * Rate);
    for (int i = from; i < s.Length; i++)
    {
        double t = (double)(i - from) / Rate, x = 2 * r.NextDouble() - 1;
        y0 = hp * (y0 + x - x0);
        x0 = x;
        z += lp * (y0 - z);
        s[i] += amp * Math.Min(1, t / attack) * Math.Exp(-t / decay) * z;
    }
}

// An air bubble's ring: a sine whose pitch rises as it nears the surface, decaying in a few periods.
static void Bubble(double[] s, Random r, double at, double low, double high, double amp)
{
    double f0 = low * Math.Pow(high / low, r.NextDouble()), tau = 0.01 + 0.025 * r.NextDouble(), phase = 0;
    int from = (int)(at * Rate);
    for (int i = from; i < Math.Min(s.Length, from + (int)(6 * tau * Rate)); i++)
    {
        double t = (double)(i - from) / Rate;
        phase += 2 * Math.PI * f0 * (1 + 0.6 * t / tau) / Rate;
        s[i] += amp * Math.Min(1, t / 0.001) * Math.Exp(-t / tau) * Math.Sin(phase);
    }
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
