using System;
using System.Collections.Generic;
using KotH.Shared.Bots;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    public sealed partial class BotProbe
    {
        /// <summary>Map for "bots reach MAP": the climbing grid's way up Atrium, stage by stage.</summary>
        public string Reach;

        // Atrium's way up (Tools/maps/Atrium.cs): name, radius, degrees, top.
        static readonly (string Name, float R, float Deg, float Top)[] AtriumStages =
        {
            ("hall", 10f, 22.5f, -5.6f), ("H", 6.6f, 22.5f, -4f), ("lower", 2.5f, 22.5f, -3.2f), ("lower'", 2.5f, 67.5f, -3.2f),
            ("M", 6.6f, 67.5f, -1.6f), ("floor", 9.6f, 67.5f, 0f), ("floor'", 9.6f, 45f, 0f), ("A", 6.2f, 45f, 1.6f),
            ("zone", 2.5f, 45f, 3.2f),
        };

        /// <summary>Prints for every stage its floor, seconds to the zone and the moves
        /// leaving it (kind, landing, cost); 0 when every stage reaches the zone.</summary>
        int Reaches(string map)
        {
            using var world = new HeadlessWorld();
            world.LoadArena(map);
            world.StartMatch();
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, new LocalFinalStates(world.Simulation), 1);
            ClimbGrid g = director.Navigation.Grid;
            Log($"[reach] {map}: {g.FloorCount} floors, {g.Drops} drop, {g.Jumps} jump, {g.DoubleJumps} double, {g.Gaps} gap");
            int lost = 0;
            foreach (var (name, r, deg, top) in AtriumStages)
                foreach (float sign in new[] { 1f, -1f })
                {
                    float a = deg * MathF.PI / 180f;
                    var feet = new Vector3(r * MathF.Cos(a), top, sign * r * MathF.Sin(a));
                    if (MathF.Abs(g.SurfaceAt(feet.X, feet.Z, top + 0.5f) - top) > 0.1f)
                        continue;
                    int f = g.FloorAt(feet);
                    float t = g.TimeOf(f);
                    lost += t == float.MaxValue ? 1 : 0;
                    Log(FormattableString.Invariant($"[reach] {name,-7} ({feet.X:F1}, {feet.Y:F1}, {feet.Z:F1}) floor {f} time {(t == float.MaxValue ? "none" : t.ToString("F1"))}"));
                    Moves(g, feet, 2.5f);
                    break;
                }
            Log(lost == 0 ? "[reach] every stage reaches the zone" : $"[reach] {lost} stages without a way");
            return lost == 0 ? 0 : 1;
        }

        // The moves leaving any floor within reach of the feet at their level, by landing height.
        void Moves(ClimbGrid g, Vector3 feet, float around)
        {
            var seen = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (float dx = -around; dx <= around; dx += 0.5f)
                for (float dz = -around; dz <= around; dz += 0.5f)
                {
                    int f = g.FloorAt(new Vector3(feet.X + dx, feet.Y, feet.Z + dz));
                    if (f < 0 || MathF.Abs(g.FloorFeet(f).Y - feet.Y) > 0.3f)
                        continue;
                    g.LinksOf(f, out int first, out int end);
                    for (int i = first; i < end; i++)
                    {
                        LinkKind kind = g.LinkAt(i, out _, out Vector3 to);
                        string key = FormattableString.Invariant($"{kind} to y {to.Y:F1} (time {Time(g.TimeOf(g.FloorAt(to)))})");
                        seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                }
            foreach (var kv in seen)
                Log($"[reach]     {kv.Value,4}× {kv.Key}");
        }

        static string Time(float t) => t == float.MaxValue ? "none" : t.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
