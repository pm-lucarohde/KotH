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
            Log($"[reach] {map}: {g.FloorCount} floors, {g.Drops} drop, {g.Jumps} jump, {g.DoubleJumps} double, {g.Gaps} gap, {g.LadderLinks} ladder");
            if (map == Maps.TheLadder)
                return LadderReach(world, director);
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
                        string key = FormattableString.Invariant($"{kind} to y {to.Y:F1} (time {TimeText(g.TimeOf(g.FloorAt(to)))})");
                        seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                }
            foreach (var kv in seen)
                Log($"[reach]     {kv.Value,4}× {kv.Key}");
        }

        // The Ladder: every spawn's seconds to the zone, and each ladder's foot and top.
        int LadderReach(HeadlessWorld world, BotDirector director)
        {
            ClimbGrid g = director.Navigation.Grid;
            int lost = 0;
            for (int slot = 0; slot < KotH.Shared.Net.NetWorld.BotSlots.Length; slot++)
            {
                Vector3 p = KotH.Shared.Net.NetWorld.SlotMarker(world.Builder, slot).Transform.Position;
                float t = g.TimeOf(g.FloorAt(p));
                lost += t == float.MaxValue ? 1 : 0;
                Log(FormattableString.Invariant($"[reach] spawn {slot} ({p.X:F1}, {p.Y:F1}, {p.Z:F1}) time {TimeText(t)}"));
            }
            // Per metre of height: floors, and those with a way to the zone.
            var all = new int[90];
            var up = new int[90];
            for (int f = 0; f < g.FloorCount; f++)
            {
                int band = Math.Clamp((int)MathF.Floor(g.FloorFeet(f).Y), 0, 89);
                all[band]++;
                up[band] += g.TimeOf(f) < float.MaxValue ? 1 : 0;
            }
            for (int band = 0; band < 90; band++)
                if (all[band] > 0)
                    Log($"[reach] height {band,2}: {up[band],4} of {all[band],4} floors reach the zone");
            // KOTH_REACH_BAND=y: every floor of that metre with its moves.
            if (int.TryParse(Environment.GetEnvironmentVariable("KOTH_REACH_BAND"), out int show))
                for (int f = 0; f < g.FloorCount; f++)
                {
                    Vector3 p = g.FloorFeet(f);
                    if ((int)MathF.Floor(p.Y) != show)
                        continue;
                    Log(FormattableString.Invariant($"[reach]   floor {f} ({p.X:F2}, {p.Y:F2}, {p.Z:F2}) time {TimeText(g.TimeOf(f))}"));
                    g.LinksOf(f, out int first, out int end);
                    for (int i = first; i < end; i++)
                    {
                        LinkKind kind = g.LinkAt(i, out _, out Vector3 to);
                        Log(FormattableString.Invariant($"[reach]     {kind} to ({to.X:F2}, {to.Y:F2}, {to.Z:F2}) cost {g.LinkCost(i):F1} time {TimeText(g.TimeOf(g.FloorAt(to)))}"));
                    }
                }
            var ladders = director.Navigation.Ladders;
            for (int i = 0; i < ladders.Count; i++)
            {
                var s = ladders[i];
                Vector3 front = s.Foot + s.Out * 0.6f, behind = new Vector3(s.Foot.X - s.Out.X * 0.5f, s.Top, s.Foot.Z - s.Out.Z * 0.5f);
                int foot = g.FloorAt(front), top = g.FloorAt(behind);
                Log(FormattableString.Invariant(
                    $"[reach] ladder {i,2} ({s.Foot.X:F1}, {s.Foot.Z:F1}) {s.Foot.Y:F1}-{s.Top:F1}: foot {(foot < 0 ? "-" : g.FloorFeet(foot).ToString())} time {TimeText(g.TimeOf(foot))}, top {(top < 0 ? "-" : g.FloorFeet(top).ToString())} surface {g.SurfaceAt(behind.X, behind.Z, s.Top + 0.5f):F2} time {TimeText(g.TimeOf(top))}"));
                Moves(g, g.FloorFeet(foot < 0 ? 0 : foot), 0.6f);
            }
            Log(lost == 0 ? "[reach] every spawn reaches the zone" : $"[reach] {lost} spawns without a way");
            return lost == 0 ? 0 : 1;
        }

        static string TimeText(float t) => t == float.MaxValue ? "none" : t.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
