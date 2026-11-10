using System;
using System.Collections.Generic;

namespace KotH.MapFormat
{
    /// <summary>A spawn: where a player starts and respawns. Without a yaw it faces the zone.</summary>
    public sealed class MapSpawn
    {
        public float X, Y, Z;
        public float? Yaw;   // degrees, Stride's: 0 looks along -Z, 90 along -X
    }

    /// <summary>The capture zone and the round's rules, as CaptureZone has them.</summary>
    public sealed class MapZone
    {
        public float X, Y = 2f, Z;
        public float SizeX = 12f, SizeY = 3.5f, SizeZ = 12f;
        public float CaptureSeconds = 3f;
        public int WinPoints = 100;
        public float TimeLimitMinutes = 20f;
        public float WinPauseSeconds = 15f;
        public float CountdownSeconds = 60f;
        public float JoinSeconds = 60f;
        public float RespawnBelow = -5f;
        public float RingInner, RingOuter, RingSpacing = 6f;
    }

    /// <summary>map.toml: what the map is and how it plays. The geometry is in map.glb (seen) and
    /// collision.bin (solid); this file holds the rest.</summary>
    public sealed class MapManifest
    {
        public const string Game = "KotH";
        public const int Format = 1;

        /// <summary>Folder name and local id: letters, digits, '-' and '_'.</summary>
        public string Name = "MyMap";
        public string Title = "My map";
        public string Author = "";
        public string Description = "";

        /// <summary>public, friends, unlisted or private (the default: a test upload stays hidden).</summary>
        public string Visibility = "private";

        /// <summary>The Steam Workshop item, 0 before the first upload.</summary>
        public ulong WorkshopId;

        public MapZone Zone = new MapZone();
        public readonly List<MapSpawn> Spawns = new List<MapSpawn>();

        /// <summary>Where the three training dummies stand, null: in front of the first spawn.</summary>
        public MapSpawn Dummies;

        /// <summary>BotSettings fields by name (as in Game Studio, e.g. JumpUp), only those changed.</summary>
        public readonly SortedDictionary<string, double> Bots = new SortedDictionary<string, double>(StringComparer.Ordinal);

        public static readonly string[] Visibilities = { "public", "friends", "unlisted", "private" };

        public static MapManifest Parse(string text)
        {
            TomlTable t = TomlTable.Parse(text);
            if (t.String("game") != Game)
                throw new FormatException("not a KotH map (game = \"KotH\" missing)");
            int format = (int)t.Number("format", 0);
            if (format != Format)
                throw new FormatException($"map format {format}, this game reads {Format}: update the game or the kit");
            var m = new MapManifest
            {
                Name = t.String("name"),
                Title = t.String("title"),
                Author = t.String("author"),
                Description = t.String("description"),
                Visibility = t.String("visibility", "private"),
            };
            string id = t.String("workshop_id", "0");
            ulong.TryParse(id, out m.WorkshopId);
            if (!ValidName(m.Name))
                throw new FormatException($"name \"{m.Name}\": only letters, digits, '-' and '_', 1 to 64");
            if (Array.IndexOf(Visibilities, m.Visibility) < 0)
                throw new FormatException($"visibility \"{m.Visibility}\": public, friends, unlisted or private");

            TomlTable z = t.Table("zone") ?? throw new FormatException("[zone] missing");
            double[] at = z.Numbers("position", 3) ?? throw new FormatException("zone position = [x, y, z] missing");
            double[] size = z.Numbers("size", 3) ?? new double[] { 12, 3.5, 12 };
            m.Zone = new MapZone
            {
                X = (float)at[0], Y = (float)at[1], Z = (float)at[2],
                SizeX = (float)size[0], SizeY = (float)size[1], SizeZ = (float)size[2],
                CaptureSeconds = (float)z.Number("capture_seconds", 3),
                WinPoints = (int)z.Number("win_points", 100),
                TimeLimitMinutes = (float)z.Number("time_limit_minutes", 20),
                WinPauseSeconds = (float)z.Number("win_pause_seconds", 15),
                CountdownSeconds = (float)z.Number("countdown_seconds", 60),
                JoinSeconds = (float)z.Number("join_seconds", 60),
                RespawnBelow = (float)z.Number("respawn_below", -5),
                RingInner = (float)z.Number("ring_inner", 0),
                RingOuter = (float)z.Number("ring_outer", 0),
                RingSpacing = (float)z.Number("ring_spacing", 6),
            };
            foreach (TomlTable s in t.Array("spawn"))
                m.Spawns.Add(Spawn(s, "spawn"));
            if (m.Spawns.Count == 0)
                throw new FormatException("no [[spawn]]: at least one spawn point");
            if (t.Table("dummies") is TomlTable d)
                m.Dummies = Spawn(d, "dummies");
            if (t.Table("bots") is TomlTable b)
                foreach (KeyValuePair<string, object> kv in b.Values)
                    if (kv.Value is double v)
                        m.Bots[kv.Key] = v;
            return m;
        }

        static MapSpawn Spawn(TomlTable s, string what)
        {
            double[] p = s.Numbers("position", 3) ?? throw new FormatException($"{what} position = [x, y, z] missing");
            return new MapSpawn { X = (float)p[0], Y = (float)p[1], Z = (float)p[2], Yaw = s.Has("yaw") ? (float)s.Number("yaw", 0) : (float?)null };
        }

        public static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64)
                return false;
            foreach (char c in name)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_'))
                    return false;
            return true;
        }

        public override string ToString()
        {
            var w = new TomlWriter()
                .Comment("A KotH map, written by the map kit. Edit in the kit and export again.")
                .Key("game", Game).Key("format", Format)
                .Key("name", Name).Key("title", Title).Key("author", Author).Key("description", Description)
                .Key("visibility", Visibility).Key("workshop_id", WorkshopId.ToString());
            MapZone z = Zone;
            w.Table("zone").Key("position", z.X, z.Y, z.Z).Key("size", z.SizeX, z.SizeY, z.SizeZ)
             .Key("capture_seconds", z.CaptureSeconds).Key("win_points", z.WinPoints)
             .Key("time_limit_minutes", z.TimeLimitMinutes).Key("win_pause_seconds", z.WinPauseSeconds)
             .Key("countdown_seconds", z.CountdownSeconds).Key("join_seconds", z.JoinSeconds)
             .Key("respawn_below", z.RespawnBelow)
             .Key("ring_inner", z.RingInner).Key("ring_outer", z.RingOuter).Key("ring_spacing", z.RingSpacing);
            if (Dummies != null)
                WriteSpawn(w.Table("dummies"), Dummies);
            if (Bots.Count > 0)
            {
                w.Table("bots");
                foreach (KeyValuePair<string, double> kv in Bots)
                    w.Key(kv.Key, kv.Value);
            }
            foreach (MapSpawn s in Spawns)
                WriteSpawn(w.ArrayItem("spawn"), s);
            return w.ToString();
        }

        static void WriteSpawn(TomlWriter w, MapSpawn s)
        {
            w.Key("position", s.X, s.Y, s.Z);
            if (s.Yaw.HasValue)
                w.Key("yaw", s.Yaw.Value);
        }
    }
}
