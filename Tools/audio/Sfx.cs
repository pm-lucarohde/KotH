#!/usr/bin/env -S dotnet run
// The game's sounds out of Luca's picks in Sounds/ (kept as they are, not in Git).
// Writes KotH.Game/Resources/Sounds/NAME.wav, 48 kHz 16 bit: silence cut off both ends,
// a short fade at the end, mono for the sounds placed in the world, every one at the same
// loudness (SFX_LOUD, the loudest 400 ms; never over PEAK), so SoundEffects' volumes alone
// set the mix. The ambient stays stereo at the music's loudness and loops seamlessly.
//
//   dotnet run Tools/audio/Sfx.cs        needs ffmpeg
#:include ../Common/Py.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KotH.Tools;

const int Rate = 48000;
const double SfxLoud = -20.0;        // LUFS over the loudest 400 ms
const double AmbientLoud = -15.5;    // LUFS integrated, as the music
const double Peak = -1.0;            // dBFS
const double Silence = -60.0;        // dBFS: quieter than this at either end is cut
const double Fade = 0.03;            // seconds faded out at the end
const double LoopFade = 1.0;         // seconds of the ambient's end laid over its start
string[] sounds = ["fall", "hit1", "hit2", "hit3", "jump", "slide", "sporejump", "steps", "wind"];
var filters = new Dictionary<string, string> { ["steps"] = "lowpass=f=1000" };   // ffmpeg filters on decoding (Luca: steps too harsh)
var dryAt = new Dictionary<string, (double, double)> { ["steps"] = (0.04, 0.12) };  // the room's reverb faded out here (Luca: sounded like a hall)

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
string source = Path.Combine(root, "Sounds");
string target = Path.Combine(root, "KotH.Game/Resources/Sounds");
if (!Directory.Exists(source))
{
    Console.Error.WriteLine("Abort: Sounds/ is missing");
    return 1;
}
Directory.CreateDirectory(target);
foreach (string name in sounds)
{
    float[] samples = Trim(Decode(Path.Combine(source, name + ".mp3"), 1, filters.GetValueOrDefault(name)), 1);
    if (dryAt.TryGetValue(name, out var span))
        samples = Dry(samples, span.Item1, span.Item2);
    double before = Loudness(samples, 1, false);
    double gain = Write(name, samples, 1, SfxLoud - before);
    Console.WriteLine($"{name,-10} {Py.F((double)samples.Length / Rate, 2)} s, {Py.F(before, 1),6} LUFS (400 ms) -> {Py.F(before + gain, 1),6}");
}
{
    float[] samples = Seamless(Decode(Path.Combine(source, "ambient.mp3"), 2, null), 2);
    double before = Loudness(samples, 2, true);
    double gain = Write("ambient", samples, 2, AmbientLoud - before);
    Console.WriteLine($"{"ambient",-10} {Py.F((double)samples.Length / Rate / 2, 2)} s, {Py.F(before, 1),6} LUFS -> {Py.F(before + gain, 1),6}, Schleife");
}
return 0;

float[] Decode(string path, int channels, string filter)
{
    var shape = filter != null ? new[] { "-af", filter } : [];
    byte[] raw = Run(["-v", "error", "-i", path, .. shape, "-f", "f32le", "-ac", $"{channels}", "-ar", $"{Rate}", "-"], null, true).Stdout;
    return MemoryMarshal.Cast<byte, float>(raw).ToArray();
}

// LUFS of the samples, integrated or the loudest momentary (400 ms) value.
double Loudness(float[] samples, int channels, bool integrated)
{
    var padded = new float[samples.Length + Rate * channels / 2];
    samples.CopyTo(padded, 0);
    string log = Run(["-v", "info", "-nostats", "-f", "f32le", "-ac", $"{channels}", "-ar", $"{Rate}", "-i", "-",
                      "-af", "ebur128", "-f", "null", "-"], MemoryMarshal.AsBytes(padded.AsSpan()).ToArray(), false).Stderr;
    if (integrated)
        return Py.Float(Regex.Matches(log, @"I:\s+(-?[\d.]+) LUFS")[^1].Groups[1].Value);
    return Regex.Matches(log, @"M:\s*(-?[\d.]+)").Max(m => Py.Float(m.Groups[1].Value));
}

// Samples are float32 as in the script's array('f'): every product is formed in double and stored back rounded.
float[] Trim(float[] samples, int channels)
{
    double floor = Math.Pow(10, Silence / 20);
    int frames = samples.Length / channels;
    var loud = Enumerable.Range(0, frames).Where(i => Enumerable.Range(0, channels).Max(c => Math.Abs((double)samples[i * channels + c])) > floor).ToList();
    int first = loud[0], last = loud[^1] + 1;
    float[] output = samples[(first * channels)..(last * channels)];
    int fade = Math.Min((int)(Fade * Rate), last - first);
    int n = last - first;
    for (int i = 0; i < fade; i++)
    {
        double k = 1.0 - (double)(i + 1) / fade;
        for (int c = 0; c < channels; c++)
            output[(n - fade + i) * channels + c] = (float)(output[(n - fade + i) * channels + c] * k);
    }
    return output;
}

// Faded out from start to end seconds (a cosine) and cut there: no room tail.
float[] Dry(float[] samples, double start, double end)
{
    int a = (int)(start * Rate), b = (int)(end * Rate);
    float[] output = samples[..Math.Min(b, samples.Length)];
    for (int i = a; i < output.Length; i++)
        output[i] = (float)(output[i] * (0.5 + 0.5 * Math.Cos(Math.PI * (i - a) / (b - a))));
    return output;
}

// The last LOOP_FADE seconds faded out over the first ones fading in, then cut off.
float[] Seamless(float[] samples, int channels)
{
    int fade = (int)(LoopFade * Rate);
    int n = samples.Length / channels;
    float[] output = samples[..((n - fade) * channels)];
    for (int i = 0; i < fade; i++)
    {
        // Equal power: the noise keeps its loudness through the seam.
        double a = Math.Sin(0.5 * Math.PI * i / fade), b = Math.Cos(0.5 * Math.PI * i / fade);
        for (int c = 0; c < channels; c++)
            output[i * channels + c] = (float)(a * samples[i * channels + c] + b * samples[(n - fade + i) * channels + c]);
    }
    return output;
}

double Write(string name, float[] samples, int channels, double gainDb)
{
    double peak = samples.Max(v => Math.Abs((double)v));
    gainDb = Math.Min(gainDb, Peak - 20 * Math.Log10(peak));
    double k = Math.Pow(10, gainDb / 20);
    short[] pcm = samples.Select(v => (short)Math.Max(-32767, Math.Min(32767, Math.Round(v * k * 32767)))).ToArray();
    Run(["-v", "error", "-y", "-f", "s16le", "-ac", $"{channels}", "-ar", $"{Rate}", "-i", "-", Path.Combine(target, name + ".wav")],
        MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray(), true);
    return gainDb;
}

static (byte[] Stdout, string Stderr) Run(string[] arguments, byte[] input, bool check)
{
    var info = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in arguments)
        info.ArgumentList.Add(a);
    using var process = Process.Start(info);
    var stdout = new MemoryStream();
    Task reading = process.StandardOutput.BaseStream.CopyToAsync(stdout);
    Task<string> errors = process.StandardError.ReadToEndAsync();
    if (input != null)
        process.StandardInput.BaseStream.Write(input);
    process.StandardInput.Close();
    reading.Wait();
    process.WaitForExit();
    if (check && process.ExitCode != 0)
        throw new Exception($"ffmpeg {string.Join(' ', arguments)}: {errors.Result}");
    return (stdout.ToArray(), errors.Result);
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
