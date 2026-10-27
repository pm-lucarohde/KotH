#!/usr/bin/env -S dotnet run
// Compares the VERDICTS of two measured states, not the numbers. Every probe has
// its own rule for what a case's verdict is (up or not, climbed or not, -> ok,
// target met ...). Prints every case whose verdict differs between the reference
// set and the new state, with both full lines.
//
//   dotnet run Tools/Verdict.cs -- ref1,ref2,ref3 new
#:include Common/Py.cs
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
    Console.WriteLine($"URTEIL   {k}\n    gestern: {(a != null ? string.Join(" | ", a) : "-")}\n    heute  : {b ?? "-"}");
}
Console.WriteLine($"--- {same + diff} Urteile, {same} gleich, {diff} anders");

static double Num(string s) => Py.Float(s.TrimEnd(',').Replace(',', '.'));

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
                v = rest.Contains("oben bei") ? "oben" : "NIE OBEN";
            else if (probe.StartsWith("SlopeJump"))
                v = "ohne Boden " + Find(rest, @"ohne Boden (\d+)");
            else if (probe.StartsWith("Step") && name.Length > 0 && "+-".Contains(name[0]))
                v = Num(Find(rest, @"max ([\d,]+)")) > 0.1 ? "steigt" : "steigt nicht";
            else if (probe.StartsWith("Step") && name.StartsWith('='))
                v = Num(Find(rest, @"end (-?[\d,]+)")) > -0.5 ? "bleibt oben" : "faellt";
            else if (probe.StartsWith("Step"))
            {
                double max = Num(Find(rest, @"max ([+-]?[\d,]+)"));
                int slid = int.Parse(Find(rest, @"Rutsch-Ticks (\d+)"));
                v = (max > 4.5 ? "oben" : "unten") + (slid != 0 ? ", rutscht" : ", rutscht nicht");
            }
            else if (probe.StartsWith("Impact"))
                v = Num(Find(rest, @"senkrecht (-?[\d,]+)")) > 1.0 ? "obendrauf" : "daneben";
            else if (probe.StartsWith("Melee"))
            {
                var pairs = Regex.Matches(rest, @"([\d,]+)\s*%?\s*\(soll (?:je )?([\d,]+)\)");
                if (pairs.Count > 0)
                    v = pairs.All(p => Num(p.Groups[1].Value) == Num(p.Groups[2].Value)) ? "soll erfuellt" : "soll VERFEHLT";
            }
            if (v != null)
                output[probe + " " + name] = v;
        }
        else if (m2.Success)
        {
            string key = Regex.Split(m2.Groups[2].Value, @"\s+nach\s+|;|:")[0].Trim();
            output[probe + " " + key[..Math.Min(40, key.Length)]] = "-> " + m2.Groups[3].Value;
        }
        else if (Regex.IsMatch(line, "ALLE F|FAELLE WIE|WIE ERWARTET"))
        {
            int at = line.IndexOf("] ", StringComparison.Ordinal);
            output[probe + " GESAMT"] = at < 0 ? line : line[(at + 2)..];
        }
    }
    return output;
}

// The first group of a match that must be there, as the script's .group(1) demanded.
static string Find(string text, string pattern)
{
    Match m = Regex.Match(text, pattern);
    if (!m.Success)
        throw new FormatException($"'{pattern}' fehlt in: {text}");
    return m.Groups[1].Value;
}
