using System;
using System.Globalization;
using System.IO;
using System.Text;
using KotH.Client.Rendering.Dlss;
using Stride.Input;

namespace KotH.Client.Settings
{
    /// <summary>The settings in ApplicationData/KotH/settings.toml (on Linux ~/.config), written and read by
    /// hand like look.toml: AOT without reflection, unknown keys and bad values keep the default. Shots and
    /// headless runs read KOTH_SETTINGS instead (none: the defaults) and never write.</summary>
    public static class SettingsFile
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        static bool Test => Environment.GetEnvironmentVariable("KOTH_SHOT") != null
                            || Environment.GetEnvironmentVariable("KOTH_HEADLESS") == "1";

        /// <summary>Where the settings are read from, null for none.</summary>
        public static string Path => Environment.GetEnvironmentVariable("KOTH_SETTINGS") is { Length: > 0 } test ? test
            : Test ? null
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KotH", "settings.toml");

        public static PlayerSettings Load() => Load(Path);

        /// <summary>The settings in that file; the defaults for what it lacks or when it is missing.</summary>
        public static PlayerSettings Load(string path)
        {
            var settings = new PlayerSettings();
            try
            {
                if (path != null && File.Exists(path))
                    Parse(File.ReadAllText(path), settings);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[settings] {e.Message}");
            }
            return settings;
        }

        public static void Save(PlayerSettings settings)
        {
            if (Test || Path is not { } path)
                return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Write(settings));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[settings] {e.Message}");
            }
        }

        public static string Write(PlayerSettings s)
        {
            var t = new StringBuilder("# KotH settings; the game rewrites this file on Apply\n");
            t.Append("\n[sound]\n");
            Number(t, "overall", s.Overall); Number(t, "effects", s.Effects); Number(t, "music", s.Music); Number(t, "ambient", s.Ambient);
            Text(t, "output_device", s.OutputDevice); Text(t, "input_device", s.InputDevice);
            t.Append("\n[graphics]\n");
            Text(t, "window", s.Window.ToString().ToLowerInvariant());
            Number(t, "width", s.Width); Number(t, "height", s.Height); Number(t, "fps_limit", s.FpsLimit);
            Flag(t, "vsync", s.VSync); Number(t, "fov", s.Fov);
            Flag(t, "dlss", s.Dlss); Text(t, "dlss_mode", s.DlssMode.ToString().ToLowerInvariant()); Number(t, "dlss_sharpness", s.DlssSharpness);
            Flag(t, "fxaa", s.Fxaa);
            Text(t, "shadows", Name(s.Shadows)); Text(t, "textures", Name(s.Textures)); Text(t, "lighting", Name(s.Lighting));
            Text(t, "effects", Name(s.EffectsQuality)); Text(t, "volumetrics", Name(s.Volumetrics));
            t.Append("\n[input]\n");
            Number(t, "mouse_sensitivity", s.MouseSensitivity);
            t.Append("\n[keys]\n");
            foreach (Binding b in Enum.GetValues<Binding>())
                Text(t, KeyName(b), s.Key(b).ToString());
            t.Append("\n[host]\n");
            Text(t, "name", s.HostName); Flag(t, "private", s.HostPrivate); Text(t, "country", s.HostCountry);
            Text(t, "languages", string.Join(",", s.HostLanguages)); Flag(t, "modded", s.HostModded);
            return t.ToString();
        }

        static string Name(Quality q) => q.ToString().ToLowerInvariant();

        static string KeyName(Binding b) => b == Binding.PushToTalk ? "push_to_talk" : b.ToString().ToLowerInvariant();

        static void Number(StringBuilder t, string key, float v) => t.Append(key).Append(" = ").Append(v.ToString("0.###", Invariant)).Append('\n');

        static void Flag(StringBuilder t, string key, bool v) => t.Append(key).Append(" = ").Append(v ? "true" : "false").Append('\n');

        static void Text(StringBuilder t, string key, string v)
            => t.Append(key).Append(" = \"").Append(v.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");

        static void Parse(string toml, PlayerSettings s)
        {
            string table = "";
            foreach (string raw in toml.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (line[0] == '[')
                {
                    table = line.Trim('[', ']', ' ');
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                string key = line[..eq].Trim();
                string value = Value(line[(eq + 1)..].Trim());
                Read(s, table, key, value);
            }
        }

        // A quoted text without its quotes and escapes, else the bare word up to a comment.
        static string Value(string v)
        {
            if (v.StartsWith('"'))
            {
                var text = new StringBuilder();
                for (int i = 1; i < v.Length && v[i] != '"'; i++)
                    text.Append(v[i] == '\\' && i + 1 < v.Length ? v[++i] : v[i]);
                return text.ToString();
            }
            int hash = v.IndexOf('#');
            return (hash >= 0 ? v[..hash] : v).Trim();
        }

        static void Read(PlayerSettings s, string table, string key, string v)
        {
            switch (table, key)
            {
                case ("sound", "overall"): s.Overall = Unit(v, s.Overall); break;
                case ("sound", "effects"): s.Effects = Unit(v, s.Effects); break;
                case ("sound", "music"): s.Music = Unit(v, s.Music); break;
                case ("sound", "ambient"): s.Ambient = Unit(v, s.Ambient); break;
                case ("sound", "output_device"): s.OutputDevice = v; break;
                case ("sound", "input_device"): s.InputDevice = v; break;
                case ("graphics", "window"): s.Window = Enum.TryParse(v, true, out WindowMode w) ? w : s.Window; break;
                case ("graphics", "width"): s.Width = Math.Max(0, Int(v, s.Width)); break;
                case ("graphics", "height"): s.Height = Math.Max(0, Int(v, s.Height)); break;
                case ("graphics", "fps_limit"): s.FpsLimit = Math.Clamp(Int(v, s.FpsLimit), 0, 1000); break;
                case ("graphics", "vsync"): s.VSync = Bool(v, s.VSync); break;
                case ("graphics", "fov"): s.Fov = Math.Clamp(Float(v, s.Fov), PlayerSettings.MinFov, PlayerSettings.MaxFov); break;
                case ("graphics", "dlss"): s.Dlss = Bool(v, s.Dlss); break;
                case ("graphics", "dlss_mode"): s.DlssMode = Rendering.Dlss.Dlss.ParseMode(v) is var m && m != DlssMode.Off ? m : s.DlssMode; break;
                case ("graphics", "dlss_sharpness"): s.DlssSharpness = Unit(v, s.DlssSharpness); break;
                case ("graphics", "fxaa"): s.Fxaa = Bool(v, s.Fxaa); break;
                case ("graphics", "shadows"): s.Shadows = Level(v, s.Shadows); break;
                case ("graphics", "textures"): s.Textures = Level(v, s.Textures); break;
                case ("graphics", "lighting"): s.Lighting = Level(v, s.Lighting); break;
                case ("graphics", "effects"): s.EffectsQuality = Level(v, s.EffectsQuality); break;
                case ("graphics", "volumetrics"): s.Volumetrics = Level(v, s.Volumetrics); break;
                case ("input", "mouse_sensitivity"):
                    s.MouseSensitivity = Math.Clamp(Float(v, s.MouseSensitivity), PlayerSettings.MinSensitivity, PlayerSettings.MaxSensitivity);
                    break;
                case ("host", "name"): s.HostName = v.Length > 40 ? v[..40] : v; break;
                case ("host", "private"): s.HostPrivate = Bool(v, s.HostPrivate); break;
                case ("host", "country"): s.HostCountry = v.Length == 2 ? v.ToUpperInvariant() : ""; break;
                case ("host", "languages"): s.HostLanguages = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case ("host", "modded"): s.HostModded = Bool(v, s.HostModded); break;
                case ("keys", _):
                    foreach (Binding b in Enum.GetValues<Binding>())
                        if (KeyName(b) == key && Enum.TryParse(v, true, out Keys k) && Enum.IsDefined(k))
                            s.Keys[(int)b] = k;
                    break;
            }
        }

        static float Float(string v, float fallback)
            => float.TryParse(v, NumberStyles.Float, Invariant, out float f) && float.IsFinite(f) ? f : fallback;

        static float Unit(string v, float fallback) => Math.Clamp(Float(v, fallback), 0f, 1f);

        static int Int(string v, int fallback) => int.TryParse(v, NumberStyles.Integer, Invariant, out int i) ? i : fallback;

        static bool Bool(string v, bool fallback) => v == "true" ? true : v == "false" ? false : fallback;

        static Quality Level(string v, Quality fallback) => Enum.TryParse(v, true, out Quality q) && Enum.IsDefined(q) ? q : fallback;
    }
}
