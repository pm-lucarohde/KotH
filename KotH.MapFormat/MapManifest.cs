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

        /// <summary>box, cylinder (the ellipse in SizeX and SizeZ) or polygon (Corners, with SizeY).</summary>
        public string Shape = "box";
        /// <summary>Polygon corners as x, z pairs in metres from the zone's centre, in order.</summary>
        public float[] Corners = Array.Empty<float>();
        public float CaptureSeconds = 3f;
        public int WinPoints = 100;
        public float TimeLimitMinutes = 20f;
        public float WinPauseSeconds = 15f;
        public float CountdownSeconds = 60f;
        public float JoinSeconds = 60f;
        public float RespawnBelow = -5f;
        public float RingInner, RingOuter, RingSpacing = 6f;
    }

    /// <summary>A ladder on a wall: its foot on the wall's face, looking out of the wall (yaw as a spawn's).
    /// Model: the export adds the game's ladder (rails and rungs); off, the maker draws one.</summary>
    public sealed class MapLadder
    {
        public float X, Y, Z, Yaw;
        public float Width = 1f, Height = 3f;
        public bool Model = true;
    }

    /// <summary>A trampoline: the middle of its top. Model: the export adds the game's trampoline and its
    /// solid block down to Y less Height; off, the maker draws one and its solid top.</summary>
    public sealed class MapTrampoline
    {
        public float X, Y, Z;
        public float Diameter = 1.2f, Height = 0.6f, Bounce = 0.9f;
        public bool Model = true;
    }

    /// <summary>A lava pit: box or cylinder round its middle, the lava's surface at its top; hits in waves
    /// as Volcano's magma. The look is the maker's (template Lava, collision None).</summary>
    public sealed class MapLava
    {
        public float X, Y, Z, Yaw;
        public float SizeX = 4f, SizeY = 1f, SizeZ = 4f;
        public string Shape = "box";
        public float DamagePerWave = 10f, WaveSeconds = 1f, RespawnAbove = 100f;
    }

    /// <summary>map.toml: what the map is and how it plays. The geometry is in map.glb (seen) and
    /// collision.bin (solid); this file holds the rest.</summary>
    public sealed class MapManifest
    {
        public const string Game = "KotH";
        public const int Format = 1;

        /// <summary>Written by maps with ladders, trampolines or lava: older games refuse them instead of
        /// playing them without. This game reads both.</summary>
        public const int FormatObjects = 2;

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

        public readonly List<MapLadder> Ladders = new List<MapLadder>();
        public readonly List<MapTrampoline> Trampolines = new List<MapTrampoline>();
        public readonly List<MapLava> Lava = new List<MapLava>();

        bool HasObjects => Ladders.Count > 0 || Trampolines.Count > 0 || Lava.Count > 0;

        public static readonly string[] Visibilities = { "public", "friends", "unlisted", "private" };
        public static readonly string[] Shapes = { "box", "cylinder", "polygon" };

        public static MapManifest Parse(string text)
        {
            TomlTable t = TomlTable.Parse(text);
            if (t.String("game") != Game)
                throw new FormatException("not a KotH map (game = \"KotH\" missing)");
            int format = (int)t.Number("format", 0);
            if (format != Format && format != FormatObjects)
                throw new FormatException($"map format {format}, this game reads {Format} and {FormatObjects}: update the game or the kit");
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
                Shape = z.String("shape", "box"),
            };
            if (Array.IndexOf(Shapes, m.Zone.Shape) < 0)
                throw new FormatException($"zone shape \"{m.Zone.Shape}\": box, cylinder or polygon");
            double[] corners = z.Numbers("corners") ?? Array.Empty<double>();
            if (m.Zone.Shape == "polygon" && (corners.Length < 6 || corners.Length % 2 != 0))
                throw new FormatException("zone corners = [x1, z1, x2, z2, x3, z3, ...]: at least 3 pairs");
            m.Zone.Corners = Array.ConvertAll(corners, c => (float)c);
            foreach (TomlTable s in t.Array("spawn"))
                m.Spawns.Add(Spawn(s, "spawn"));
            if (m.Spawns.Count == 0)
                throw new FormatException("no [[spawn]]: at least one spawn point");
            if (t.Table("dummies") is TomlTable d)
                m.Dummies = Spawn(d, "dummies");
            foreach (TomlTable l in t.Array("ladder"))
            {
                double[] p = l.Numbers("position", 3) ?? throw new FormatException("ladder position = [x, y, z] missing");
                m.Ladders.Add(new MapLadder
                {
                    X = (float)p[0], Y = (float)p[1], Z = (float)p[2], Yaw = (float)l.Number("yaw", 0),
                    Width = (float)l.Number("width", 1), Height = (float)l.Number("height", 3), Model = l.Bool("model", true),
                });
            }
            foreach (TomlTable a in t.Array("trampoline"))
            {
                double[] p = a.Numbers("position", 3) ?? throw new FormatException("trampoline position = [x, y, z] missing");
                m.Trampolines.Add(new MapTrampoline
                {
                    X = (float)p[0], Y = (float)p[1], Z = (float)p[2], Diameter = (float)a.Number("diameter", 1.2),
                    Height = (float)a.Number("height", 0.6), Bounce = (float)a.Number("bounce", 0.9), Model = a.Bool("model", true),
                });
            }
            foreach (TomlTable v in t.Array("lava"))
            {
                double[] p = v.Numbers("position", 3) ?? throw new FormatException("lava position = [x, y, z] missing");
                double[] extent = v.Numbers("size", 3) ?? new double[] { 4, 1, 4 };
                var lava = new MapLava
                {
                    X = (float)p[0], Y = (float)p[1], Z = (float)p[2], Yaw = (float)v.Number("yaw", 0),
                    SizeX = (float)extent[0], SizeY = (float)extent[1], SizeZ = (float)extent[2], Shape = v.String("shape", "box"),
                    DamagePerWave = (float)v.Number("damage_per_wave", 10), WaveSeconds = (float)v.Number("wave_seconds", 1),
                    RespawnAbove = (float)v.Number("respawn_above", 100),
                };
                if (lava.Shape != "box" && lava.Shape != "cylinder")
                    throw new FormatException($"lava shape \"{lava.Shape}\": box or cylinder");
                m.Lava.Add(lava);
            }
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
                .Key("game", Game).Key("format", HasObjects ? FormatObjects : Format)
                .Key("name", Name).Key("title", Title).Key("author", Author).Key("description", Description)
                .Key("visibility", Visibility).Key("workshop_id", WorkshopId.ToString());
            MapZone z = Zone;
            w.Table("zone").Key("position", z.X, z.Y, z.Z).Key("size", z.SizeX, z.SizeY, z.SizeZ)
             .Key("capture_seconds", z.CaptureSeconds).Key("win_points", z.WinPoints)
             .Key("time_limit_minutes", z.TimeLimitMinutes).Key("win_pause_seconds", z.WinPauseSeconds)
             .Key("countdown_seconds", z.CountdownSeconds).Key("join_seconds", z.JoinSeconds)
             .Key("respawn_below", z.RespawnBelow)
             .Key("ring_inner", z.RingInner).Key("ring_outer", z.RingOuter).Key("ring_spacing", z.RingSpacing);
            // A box writes neither, so box maps read the same in games before shapes.
            if (z.Shape != "box")
                w.Key("shape", z.Shape);
            if (z.Shape == "polygon")
                w.Key("corners", Array.ConvertAll(z.Corners, c => (double)c));
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
            foreach (MapLadder l in Ladders)
                w.ArrayItem("ladder").Key("position", l.X, l.Y, l.Z).Key("yaw", l.Yaw).Key("width", l.Width)
                 .Key("height", l.Height).Key("model", l.Model);
            foreach (MapTrampoline a in Trampolines)
                w.ArrayItem("trampoline").Key("position", a.X, a.Y, a.Z).Key("diameter", a.Diameter)
                 .Key("height", a.Height).Key("bounce", a.Bounce).Key("model", a.Model);
            foreach (MapLava v in Lava)
                w.ArrayItem("lava").Key("shape", v.Shape).Key("position", v.X, v.Y, v.Z).Key("size", v.SizeX, v.SizeY, v.SizeZ)
                 .Key("yaw", v.Yaw).Key("damage_per_wave", v.DamagePerWave).Key("wave_seconds", v.WaveSeconds)
                 .Key("respawn_above", v.RespawnAbove);
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
