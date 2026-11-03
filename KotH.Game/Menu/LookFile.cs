using System;
using System.Globalization;
using System.IO;
using System.Text;
using KotH.Shared.Net;

namespace KotH.Client.Menu
{
    /// <summary>The player's colours in ApplicationData/KotH/look.toml (on Linux ~/.config): a
    /// [look] table of "#RRGGBB" strings. Read and written by hand, AOT without reflection;
    /// unknown keys, comments and bad lines are skipped, a missing part keeps its default.</summary>
    public static class LookFile
    {
        static readonly string[] Keys = { "cap", "skin", "leaves", "gloves", "spores" };

        public static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KotH", "look.toml");

        public static FigureLook Load()
        {
            FigureLook look = FigureLook.Default;
            try
            {
                if (File.Exists(Path))
                    look = Parse(File.ReadAllText(Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[look] {e.Message}");
            }
            return look;
        }

        public static void Save(FigureLook look)
        {
            var text = new StringBuilder("# KotH: your mushroom's colours\n[look]\n");
            for (int i = 0; i < FigureLook.Parts; i++)
                text.Append(Keys[i]).Append(" = \"").Append(Hex(look[i])).Append("\"\n");
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, text.ToString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[look] {e.Message}");
            }
        }

        public static string Hex(uint rgb) => "#" + rgb.ToString("X6", CultureInfo.InvariantCulture);

        /// <summary>"#RRGGBB" or "RRGGBB" to 0xRRGGBB; null if it is neither.</summary>
        public static uint? ParseHex(string text)
        {
            text = text.Trim().TrimStart('#');
            return text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : null;
        }

        static FigureLook Parse(string toml)
        {
            FigureLook look = FigureLook.Default;
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
                if (table != "look" || eq < 0)
                    continue;
                int part = Array.IndexOf(Keys, line[..eq].Trim());
                string value = line[(eq + 1)..];
                // Up to the closing quote: a comment may follow.
                int close = value.IndexOf('"', value.IndexOf('"') + 1);
                if (close > 0)
                    value = value[..(close + 1)];
                if (part >= 0 && ParseHex(value.Trim().Trim('"')) is { } rgb)
                    look[part] = rgb;
            }
            return look;
        }
    }
}
