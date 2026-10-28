#!/usr/bin/env -S dotnet run
// Compares the VERDICTS of two measured states, not the numbers, by each probe's own rule (up
// or not, climbed or not, ok, target met ...); prints every differing case with both lines.
//   dotnet run Tools/Verdict.cs -- ref1,ref2,ref3 new
#:include Common/Py.cs
#:include Common/Libm.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KotH.Tools;

string[] refs = args[0].Split(',');
string fresh = args[1];
var reference = new Dictionary<string, SortedSet<string>>();
foreach (string r in refs)
    foreach (string fn in Directory.GetFiles(r).Select(Path.GetFileName))
        if (fn.EndsWith(".txt"))
            foreach (var (k, v) in Verdicts(Path.Combine(r, fn)))
            {
                if (!reference.TryGetValue(k, out var set))
                    reference[k] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(v);
            }
var neu = new Dictionary<string, string>();
foreach (string fn in Directory.GetFiles(fresh).Select(Path.GetFileName))
    if (fn.EndsWith(".txt"))
        foreach (var (k, v) in Verdicts(Path.Combine(fresh, fn)))
            neu[k] = v;

int same = 0, diff = 0;
foreach (string k in reference.Keys.Union(neu.Keys).Order(StringComparer.Ordinal))
{
    reference.TryGetValue(k, out var a);
    neu.TryGetValue(k, out string b);
    if (a != null && b != null && a.Contains(b))
    {
        same++;
        continue;
    }
    diff++;
    Console.WriteLine($"VERDICT  {k}\n    before: {(a != null ? string.Join(" | ", a) : "-")}\n    now   : {b ?? "-"}");
}
Console.WriteLine($"--- {same + diff} verdicts, {same} same, {diff} different");

static double Num(string s) => Py.Float(s.TrimEnd('.'));

static Dictionary<string, string> Verdicts(string path)
{
    string probe = Path.GetFileName(path);
    var output = new Dictionary<string, string>();
    foreach (string line in File.ReadLines(path))
    {
        if (line.Contains(" ~ "))
            continue;
        Match m = Regex.Match(line, @"^\[(\w+)\] == ([^:]+):(.*)$");
        Match m2 = Regex.Match(line, @"^\[(\w+)\]\s+(.+?)\s+->\s+(\S+)\s*$");
        if (m.Success)
        {
            string name = m.Groups[2].Value.Trim(), rest = m.Groups[3].Value;
            string v = null;
            if (probe.StartsWith("Crest"))
                v = rest.Contains("top at") ? "top" : "NEVER ON TOP";
            else if (probe.StartsWith("SlopeJump"))
                v = "without ground " + Find(rest, @"without ground (\d+)");
            else if (probe.StartsWith("Step") && name.Length > 0 && "+-".Contains(name[0]))
                v = Num(Find(rest, @"max ([\d.]+)")) > 0.1 ? "climbs" : "does not climb";
            else if (probe.StartsWith("Step") && name.StartsWith('='))
                v = Num(Find(rest, @"end (-?[\d.]+)")) > -0.5 ? "stays on top" : "falls";
            else if (probe.StartsWith("Step"))
            {
                double max = Num(Find(rest, @"max ([+-]?[\d.]+)"));
                int slid = int.Parse(Find(rest, @"slide ticks (\d+)"));
                v = (max > 4.5 ? "top" : "bottom") + (slid != 0 ? ", slides" : ", does not slide");
            }
            else if (probe.StartsWith("Impact"))
                v = Num(Find(rest, @"vertical (-?[\d.]+)")) > 1.0 ? "on top" : "beside";
            else if (probe.StartsWith("Melee"))
            {
                var pairs = Regex.Matches(rest, @"([\d.]+)\s*%?\s*\(want (?:each )?([\d.]+)\)");
                if (pairs.Count > 0)
                    v = pairs.All(p => Num(p.Groups[1].Value) == Num(p.Groups[2].Value)) ? "target met" : "target MISSED";
            }
            if (v != null)
                output[probe + " " + name] = v;
        }
        else if (m2.Success)
        {
            string key = Regex.Split(m2.Groups[2].Value, @"\s+after\s+|;|:")[0].Trim();
            output[probe + " " + key[..Math.Min(40, key.Length)]] = "-> " + m2.Groups[3].Value;
        }
        else if (Regex.IsMatch(line, "ALL CASES"))
        {
            int at = line.IndexOf("] ", StringComparison.Ordinal);
            output[probe + " TOTAL"] = at < 0 ? line : line[(at + 2)..];
        }
    }
    return output;
}

// The first group of a match that must be there, as the script's .group(1) demanded.
static string Find(string text, string pattern)
{
    Match m = Regex.Match(text, pattern);
    if (!m.Success)
        throw new FormatException($"'{pattern}' missing in: {text}");
    return m.Groups[1].Value;
}
