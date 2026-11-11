using System;
using System.Globalization;
using System.IO;
using KotH.Client.Steam;
using KotH.Shared.Match;
using KotH.Shared.Net;

namespace KotH.Client.Menu
{
    /// <summary>The last XP and wins Steam told, in ApplicationData/KotH/progress.toml: only shown
    /// until Steam answers (or without it), never uploaded. Written by hand like LookFile.</summary>
    public static class ProgressFile
    {
        public static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KotH", "progress.toml");

        public static (int Xp, int Wins) Load()
        {
            int xp = 0, wins = 0;
            try
            {
                if (!File.Exists(Path))
                    return (0, 0);
                foreach (string raw in File.ReadAllLines(Path))
                {
                    int eq = raw.IndexOf('=');
                    if (eq < 0 || raw.TrimStart().StartsWith('#'))
                        continue;
                    string key = raw[..eq].Trim();
                    string value = raw[(eq + 1)..].Split('#')[0].Trim();
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0)
                    {
                        if (key == "xp")
                            xp = v;
                        else if (key == "wins")
                            wins = v;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[progress] {e.Message}");
            }
            return (xp, wins);
        }

        public static void Save(int xp, int wins)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, string.Create(CultureInfo.InvariantCulture,
                    $"# KotH: your progress as Steam last told it (Steam keeps the real one)\n[progress]\nxp = {xp}\nwins = {wins}\n"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[progress] {e.Message}");
            }
        }
    }

    /// <summary>The player's XP, level and wins as the menu shows them: Steam's once known, else the file;
    /// KOTH_XP=N pretends (shots, nothing saved). Below level 10 the figure keeps its painted colours.</summary>
    public static class PlayerProgress
    {
        static readonly int? Pretend = int.TryParse(Environment.GetEnvironmentVariable("KOTH_XP"), NumberStyles.Integer,
                                                    CultureInfo.InvariantCulture, out int xp) ? Math.Max(xp, 0) : null;
        static (int Xp, int Wins)? cached;

        public static int Xp => Pretend ?? (SteamStats.Known ? SteamStats.Xp : Cached.Xp);
        public static int Wins => SteamStats.Known ? SteamStats.Wins : Cached.Wins;

        /// <summary>Whether the values are Steam's (or pretended), not the file's.</summary>
        public static bool Live => Pretend.HasValue || SteamStats.Known;

        public static int Level => Levels.LevelOf(Xp);
        public static bool CanCustomize => Level >= Levels.CustomizeLevel;

        /// <summary>The colours the figure shows: the saved ones from level 10, else the painted ones.</summary>
        public static FigureLook Shown(FigureLook saved) => CanCustomize ? saved : FigureLook.Default;

        static (int Xp, int Wins) Cached => cached ??= ProgressFile.Load();
    }
}
