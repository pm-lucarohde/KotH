#!/usr/bin/env -S dotnet run
// Compares the NUMBERS of a probe run with tolerance against a reference set.
// Every line is matched by its case name, tick traces (~) by case and tick. The
// text without numbers must be equal (else STRUCTURE); every number may be 5 mm
// (0.005) off the nearest reference value or lie between the reference runs' values
// (Ramp45/55 already scatter in Unity).
//
// What holds only 1 % is reported as ONLY 1 % (fine for speeds and percent, never for
// coordinates); one unit in the last printed digit counts as display rounding (Mono
// rounded the 7-digit text). Everything is reported with both lines, to be judged by hand.
//
//   dotnet run Tools/Regress.cs -- ref1,ref2,ref3 new [--all]
#:include Common/Py.cs
#:include Common/Libm.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KotH.Tools;

const double Abs = 0.005, Rel = 0.01;
var number = new Regex(@"(?<![\w])[+-]?\d+(?:[.,]\d+)?(?:E[+-]?\d+)?");

string[] refdirs = args[0].Split(',');
string newdir = args[1];
bool showAll = args.Contains("--all");
var totals = new Counter();

foreach (string fn in Directory.GetFiles(newdir).Select(Path.GetFileName).Order(StringComparer.Ordinal))
{
    if (!fn.EndsWith(".txt"))
        continue;
    var refs = refdirs.Where(d => File.Exists(Path.Combine(d, fn))).Select(d => Cases(Path.Combine(d, fn))).ToList();
    if (refs.Count == 0)
    {
        Console.WriteLine($"NO REFERENCE    {fn}");
        continue;
    }
    var fresh = Cases(Path.Combine(newdir, fn));
    var report = new List<string>();
    var stats = new Counter();

    foreach (var (k, line) in fresh)
    {
        stats["cases"]++;
        var known = refs.Where(r => r.Index.ContainsKey(k)).Select(r => r.Index[k]).ToList();
        if (known.Count == 0)
        {
            stats["new"]++;
            report.Add($"NEW       {k}\n    new : {line}");
            continue;
        }
        var sameShape = known.Where(r => Skeleton(r) == Skeleton(line)).ToList();
        if (sameShape.Count == 0)
        {
            stats["structure"]++;
            report.Add($"STRUCTURE {k}\n    new : {line}\n    ref : {string.Join("\n    ref : ", known)}");
            continue;
        }
        var vals = Numbers(line);
        var places = LastPlace(line);
        var refvals = sameShape.Select(Numbers).ToList();
        var bad = new List<string>();
        var rel = new List<string>();
        var shown = new List<string>();
        for (int i = 0; i < vals.Count; i++)
        {
            double v = vals[i];
            var rv = refvals.Select(r => r[i]).ToList();
            if ((rv.Min() <= v && v <= rv.Max()) || rv.Any(x => Math.Abs(v - x) <= Abs))
                continue;
            string refText = string.Join("/", rv.Distinct().Order().Select(x => Py.G(x)));
            // One unit in the last printed digit of a half-way value:
            // Mono rounded the float's 7-digit text, .NET the exact value.
            if (places[i] > 0 && rv.Any(x => Math.Abs(Math.Abs(v - x) - places[i]) < places[i] * 1e-3) && places[i] <= 0.01)
            {
                shown.Add($"#{i + 1}: {Py.G(v)} instead of {refText}");
                continue;
            }
            double nearest = rv.MinBy(x => Math.Abs(x - v));
            string text = $"#{i + 1}: {Py.G(v)} instead of {refText} (off by {Py.G(Math.Abs(v - nearest), 4)})";
            // 1 % only holds for speeds, ticks and percent, never for a
            // coordinate; which one a number is, a human decides.
            (rv.Any(x => Math.Abs(v - x) <= Rel * Math.Max(Math.Abs(v), Math.Abs(x))) ? rel : bad).Add(text);
        }
        if (shown.Count > 0 && bad.Count == 0 && rel.Count == 0)
        {
            stats["display"]++;
            if (showAll)
                report.Add($"DISPLAY   {k}\n    {string.Join("; ", shown)}");
        }
        if (bad.Count > 0 || rel.Count > 0)
        {
            stats[bad.Count > 0 ? "over" : "relative"]++;
            report.Add($"{(bad.Count > 0 ? "TOLERANCE" : "ONLY 1 % ")}  {k}\n    {string.Join("; ", bad.Concat(rel))}\n    new : {line}\n    ref : {string.Join("\n    ref : ", sameShape)}");
        }
        else if (shown.Count == 0)
        {
            stats["ok"]++;
            if (showAll && !known.Contains(line))
                report.Add($"ok        {k}\n    new : {line}\n    ref : {known[0]}");
        }
    }

    var missing = refs.SelectMany(r => r.Index.Keys).Distinct().Where(k => !fresh.Index.ContainsKey(k));
    foreach (string k in missing.Order(StringComparer.Ordinal))
    {
        stats["missing"]++;
        report.Add($"MISSING   {k}");
    }

    Console.WriteLine($"=== {fn}: {Summary(stats)}");
    foreach (string r in report)
        Console.WriteLine(r);
    totals.Add(stats);
}

Console.WriteLine($"--- total: {Summary(totals)}");

static string Summary(Counter s) =>
    $"{s["cases"]} cases, {s["ok"]} in tolerance, {s["display"]} display rounding, {s["relative"]} only in 1 %, {s["over"]} over, " +
    $"{s["structure"]} structure, {s["new"]} new, {s["missing"]} missing";

string KeyOf(string line)
{
    Match m = Regex.Match(line, @"^\[(\w+)\] == ([^:]+):(.*)$");
    if (m.Success)
        return "== " + m.Groups[2].Value.Trim();
    m = Regex.Match(line, @"^\[(\w+)\] (.+)$");
    if (m.Success && m.Groups[2].Value.StartsWith('~'))
        // Tick traces: case name and tick, e.g. "~ Ramp45 stand t003".
        return "~~ " + Regex.Split(m.Groups[2].Value, @" y |:|\|")[0].Trim();
    if (m.Success)
    {
        string head = number.Replace(m.Groups[2].Value.Split(':')[0], "#");
        return "-- " + head[..Math.Min(40, head.Length)];
    }
    return null;
}

Lines Cases(string path)
{
    var output = new Lines();
    var seen = new Counter();
    foreach (string line in File.ReadLines(path))
    {
        if (line.Contains("entering play mode"))
            continue;
        string k = KeyOf(line);
        if (k == null)
            continue;
        seen[k]++;
        output.Set(seen[k] > 1 ? $"{k} #{seen[k]}" : k, line);
    }
    return output;
}

List<double> Numbers(string text) => number.Matches(text).Select(n => Py.Float(n.Value.Replace(',', '.'))).ToList();

// Size of one unit in the last printed digit of every number.
List<double> LastPlace(string text) => number.Matches(text).Select(n =>
{
    Match m = Regex.Match(n.Value, @"[.,](\d+)");
    return m.Success && !n.Value.Contains('E') ? Math.Pow(10.0, -m.Groups[1].Length) : 0.0;
}).ToList();

string Skeleton(string text) => number.Replace(text, "#");

/// <summary>Python's Counter: missing keys count 0.</summary>
sealed class Counter : Dictionary<string, int>
{
    public new int this[string key]
    {
        get => TryGetValue(key, out int n) ? n : 0;
        set => base[key] = value;
    }

    public void Add(Counter other)
    {
        foreach (var (k, n) in other)
            this[k] += n;
    }
}

/// <summary>An ordered dict of case to line, as the script's OrderedDict.</summary>
sealed class Lines : List<(string Key, string Line)>
{
    public readonly Dictionary<string, string> Index = new();

    public void Set(string key, string line)
    {
        if (Index.ContainsKey(key))
            this[FindIndex(e => e.Key == key)] = (key, line);
        else
            Add((key, line));
        Index[key] = line;
    }
}
