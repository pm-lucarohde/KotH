#!/usr/bin/env -S dotnet run
// Compares a probe run against a SET of reference runs: a case counts as unchanged
// if its result occurs in the set. Needed because Ramp45 and Ramp55 standing jump
// between two results on their own; one reference run would raise false alarms.
//
//   dotnet run Tools/Compare.cs -- ref1,ref2,ref3 new
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

string[] refdirs = args[0].Split(',');
string newdir = args[1];
int total = 0, same = 0, spread = 0, changed = 0;
var report = new List<string>();

foreach (string fn in Directory.GetFiles(newdir).Select(Path.GetFileName).Order(StringComparer.Ordinal))
{
    if (!fn.EndsWith(".txt"))
        continue;
    var refsets = new Dictionary<string, SortedSet<string>>();
    foreach (string d in refdirs)
    {
        string p = Path.Combine(d, fn);
        if (File.Exists(p))
            foreach (var (k, v) in Cases(p))
            {
                if (!refsets.TryGetValue(k, out var set))
                    refsets[k] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(v);
            }
    }
    foreach (var (k, v) in Cases(Path.Combine(newdir, fn)))
    {
        total++;
        if (!refsets.TryGetValue(k, out var known))
        {
            changed++;
            report.Add($"NEU      {fn} {k}");
        }
        else if (known.Contains(v))
        {
            same++;
            if (known.Count > 1)
                spread++;
        }
        else
        {
            changed++;
            report.Add($"CHANGED  {fn} {k}\n           new : {v}\n           ref : {string.Join("\n           ref : ", known)}");
        }
    }
}

Console.WriteLine(report.Count > 0 ? string.Join("\n", report) : "no deviation");
Console.WriteLine($"--- {total} cases: {same} as reference ({spread} of them scatter within the reference),{changed} changed");

static List<(string, string)> Cases(string path)
{
    // In file order; a later line of the same case replaces the earlier one in place.
    var keys = new List<string>();
    var values = new Dictionary<string, string>();
    foreach (string line in File.ReadLines(path))
    {
        if (line.Contains(" ~ "))   // tick traces are diagnostics
            continue;
        string key, value;
        Match m = Regex.Match(line, @"^\[(\w+)\] == ([^:]+):(.*)$");
        if (m.Success)
            (key, value) = ("== " + m.Groups[2].Value, m.Groups[3].Value.Trim());
        else if ((m = Regex.Match(line, @"^\[(\w+)\] (.+)$")).Success)
        {
            string head = m.Groups[2].Value.Split(':')[0];
            (key, value) = ("-- " + head[..Math.Min(40, head.Length)], m.Groups[2].Value);
        }
        else
            continue;
        if (!values.ContainsKey(key))
            keys.Add(key);
        values[key] = value;
    }
    return keys.Select(k => (k, values[k])).ToList();
}
