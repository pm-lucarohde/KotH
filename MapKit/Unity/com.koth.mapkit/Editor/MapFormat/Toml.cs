using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KotH.MapFormat
{
    /// <summary>The TOML a map.toml needs, by hand (AOT, no reflection): tables, arrays of tables,
    /// strings, numbers, booleans and flat arrays of numbers. A bad line is an error with its line
    /// number, not a silent default: the map maker should see it.</summary>
    public sealed class TomlTable
    {
        public readonly Dictionary<string, object> Values = new Dictionary<string, object>();
        public readonly Dictionary<string, TomlTable> Tables = new Dictionary<string, TomlTable>();
        public readonly Dictionary<string, List<TomlTable>> Arrays = new Dictionary<string, List<TomlTable>>();

        public TomlTable Table(string name) => Tables.TryGetValue(name, out TomlTable t) ? t : null;

        public List<TomlTable> Array(string name) => Arrays.TryGetValue(name, out List<TomlTable> a) ? a : new List<TomlTable>();

        public bool Has(string key) => Values.ContainsKey(key);

        public string String(string key, string fallback = "")
            => Values.TryGetValue(key, out object v) && v is string s ? s : fallback;

        public double Number(string key, double fallback)
            => Values.TryGetValue(key, out object v) && v is double d ? d : fallback;

        public bool Bool(string key, bool fallback)
            => Values.TryGetValue(key, out object v) && v is bool b ? b : fallback;

        /// <summary>A flat array of numbers, null when missing or of another length.</summary>
        public double[] Numbers(string key, int length)
            => Values.TryGetValue(key, out object v) && v is double[] a && a.Length == length ? a : null;

        public static TomlTable Parse(string text)
        {
            var root = new TomlTable();
            TomlTable current = root;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = StripComment(lines[n]).Trim();
                if (line.Length == 0)
                    continue;
                try
                {
                    if (line.StartsWith("[["))
                    {
                        string name = Header(line, "[[", "]]");
                        if (!root.Arrays.TryGetValue(name, out List<TomlTable> list))
                            root.Arrays[name] = list = new List<TomlTable>();
                        current = new TomlTable();
                        list.Add(current);
                    }
                    else if (line.StartsWith("["))
                    {
                        string name = Header(line, "[", "]");
                        if (!root.Tables.TryGetValue(name, out current))
                            root.Tables[name] = current = new TomlTable();
                    }
                    else
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                            throw new FormatException("expected key = value");
                        string key = line.Substring(0, eq).Trim().Trim('"');
                        current.Values[key] = Value(line.Substring(eq + 1).Trim());
                    }
                }
                catch (FormatException e)
                {
                    throw new FormatException($"line {n + 1}: {e.Message}");
                }
            }
            return root;
        }

        static string Header(string line, string open, string close)
        {
            if (!line.EndsWith(close))
                throw new FormatException($"expected {close}");
            string name = line.Substring(open.Length, line.Length - open.Length - close.Length).Trim();
            if (name.Length == 0)
                throw new FormatException("empty table name");
            return name;
        }

        static string StripComment(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && quoted)
                    i++;
                else if (c == '"')
                    quoted = !quoted;
                else if (c == '#' && !quoted)
                    return line.Substring(0, i);
            }
            return line;
        }

        static object Value(string v)
        {
            if (v.StartsWith("\""))
                return Unquote(v);
            if (v == "true" || v == "false")
                return v == "true";
            if (v.StartsWith("["))
            {
                if (!v.EndsWith("]"))
                    throw new FormatException("expected ]");
                string inner = v.Substring(1, v.Length - 2).Trim();
                if (inner.Length == 0)
                    return new double[0];
                string[] parts = inner.Split(',');
                var numbers = new List<double>();
                foreach (string p in parts)
                    if (p.Trim().Length > 0)
                        numbers.Add(Number(p.Trim()));
                return numbers.ToArray();
            }
            return Number(v);
        }

        static double Number(string v)
        {
            if (!double.TryParse(v.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new FormatException($"not a number: {v}");
            return d;
        }

        static string Unquote(string v)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '"')
                {
                    if (v.Substring(i + 1).Trim().Length > 0)
                        throw new FormatException("text after the closing quote");
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (++i >= v.Length)
                    break;
                switch (v[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u':
                        if (i + 4 >= v.Length)
                            throw new FormatException("short \\u escape");
                        sb.Append((char)Convert.ToInt32(v.Substring(i + 1, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(v[i]); break;
                }
            }
            throw new FormatException("missing closing quote");
        }
    }

    /// <summary>Writes TOML lines in a fixed order and number form ("R"), so a package is the same
    /// bytes on every export of the same scene.</summary>
    public sealed class TomlWriter
    {
        readonly StringBuilder sb = new StringBuilder();

        public TomlWriter Comment(string text) { sb.Append("# ").Append(text).Append('\n'); return this; }

        public TomlWriter Table(string name) { sb.Append('\n').Append('[').Append(name).Append("]\n"); return this; }

        public TomlWriter ArrayItem(string name) { sb.Append('\n').Append("[[").Append(name).Append("]]\n"); return this; }

        public TomlWriter Key(string key, string value)
        {
            sb.Append(key).Append(" = \"");
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
            }
            sb.Append("\"\n");
            return this;
        }

        public TomlWriter Key(string key, double value) { sb.Append(key).Append(" = ").Append(Num(value)).Append('\n'); return this; }

        public TomlWriter Key(string key, bool value) { sb.Append(key).Append(" = ").Append(value ? "true" : "false").Append('\n'); return this; }

        public TomlWriter Key(string key, params double[] values)
        {
            sb.Append(key).Append(" = [");
            for (int i = 0; i < values.Length; i++)
                sb.Append(i == 0 ? "" : ", ").Append(Num(values[i]));
            sb.Append("]\n");
            return this;
        }

        /// <summary>Floats as floats: the shortest text that reads back to the same float.</summary>
        static string Num(double v)
        {
            float f = (float)v;
            return f == v ? f.ToString("R", CultureInfo.InvariantCulture) : v.ToString("R", CultureInfo.InvariantCulture);
        }

        public override string ToString() => sb.ToString();
    }
}
