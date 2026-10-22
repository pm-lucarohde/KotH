using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using KotH.Shared.Bots;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    public sealed partial class BotProbe
    {
        /// <summary>Seeds for "bots climb N": three bots up the mountain of map Neu.</summary>
        public int Climb = 0;

        const int ClimbTicks = 40 * 360;
        const float ClimbGoal = 140f;
        static readonly float[] Heights = { 20f, 50f, 90f };
        readonly int[] HopStats = new int[3], Predictions = new int[2];
        double PredictionError;
        readonly int[] PredClass = new int[4];
        readonly double[] PredClassErr = new double[4], PredClassErrY = new double[4];

        /// <summary>
        /// The climb on map Neu (the one probe off the test ground): each
        /// round runs until all three bots have been in the zone, at most
        /// six minutes. The measure is Luca's: each bot's time to its first
        /// step into the zone, averaged over the three and then the seeds.
        /// </summary>
        int Climbs(int seeds)
        {
            string pathFile = Environment.GetEnvironmentVariable("KOTH_BOTPATH");
            using StreamWriter path = pathFile != null ? new StreamWriter(pathFile) : null;
            path?.WriteLine("seed,tick,bot,x,y,z");

            float sumMean = 0f, worstMean = 0f, worstBot = 0f, sumSpread = 0f;
            int spreads = 0, missing = 0;
            var ticks = new List<double>();
            for (int k = 1; k <= seeds; k++)
            {
                int seed = 1000 + k;
                float[] times = ClimbRound(seed, path, ticks, out float spread, out string extra);
                float mean = 0f;
                var parts = new List<string>();
                foreach (float t in times)
                {
                    parts.Add(t < 0f ? "nicht oben" : $"{t:F0} s");
                    mean += t < 0f ? ClimbTicks / 40f : t;
                    missing += t < 0f ? 1 : 0;
                    worstBot = MathF.Max(worstBot, t < 0f ? ClimbTicks / 40f : t);
                }
                mean /= times.Length;
                sumMean += mean;
                worstMean = MathF.Max(worstMean, mean);
                if (spread >= 0f)
                {
                    sumSpread += spread;
                    spreads++;
                }
                Log($"[klettern] Seed {seed}: {string.Join(", ", parts)}, Mittel {mean:F0} s, " +
                    $"Streuung {(spread >= 0f ? $"{spread:F0}°" : "-")}; {extra}");
            }

            ticks.Sort();
            double Pick(double q) => ticks.Count == 0 ? 0 : ticks[Math.Min(ticks.Count - 1, (int)(q * ticks.Count))];
            float average = sumMean / seeds;
            Log($"[klettern] über {seeds} Seeds: Mittel {average:F1} s (Ziel höchstens {ClimbGoal:F0} s), " +
                $"langsamster Seed {worstMean:F0} s, langsamster Bot {worstBot:F0} s, {missing} nicht oben, " +
                $"Streuung der Routen {(spreads > 0 ? sumSpread / spreads : 0f):F0}°");
            Log($"[klettern] Sprünge gelandet: {HopStats[1]} von {HopStats[0]}, {HopStats[2]} aufgegeben vor dem Absprung");
            Log($"[klettern] Vorhersage: Boden falsch in {Predictions[1] * 100.0 / Math.Max(1, Predictions[0]):F1} % der Ticks, " +
                $"Lage im Mittel {PredictionError * 100.0 / Math.Max(1, Predictions[0]):F1} cm daneben");
            string[] names = { "Boden/Boden", "Boden/Luft", "Luft/Boden", "Luft/Luft" };
            for (int c = 0; c < 4; c++)
                Log($"[klettern]   echt/Modell {names[c]}: {PredClass[c]} Ticks, {PredClassErr[c] * 100 / Math.Max(1, PredClass[c]):F1} cm, davon Höhe {PredClassErrY[c] * 100 / Math.Max(1, PredClass[c]):F1} cm");
            Log($"[klettern] Tick mit 3 Bots: Median {Pick(0.5):F0} µs, 99 % {Pick(0.99):F0} µs, höchstens {Pick(1.0):F0} µs");
            bool passed = missing == 0 && average <= ClimbGoal;
            Log(passed ? "[klettern] BESTANDEN" : "[klettern] NICHT BESTANDEN");
            return passed ? 0 : 1;
        }

        // One round; times in seconds, -1 for a bot that never got up. The
        // spread is the mean angle round the mountain between two bots where
        // they first passed the heights above, -1 when none were passed.
        float[] ClimbRound(int seed, StreamWriter path, List<double> ticks, out float spread, out string extra)
        {
            using var world = new HeadlessWorld();
            world.LoadArena(Maps.Default);
            HillMatch match = world.StartMatch();
            var states = new LocalFinalStates(world.Simulation);
            long started = Stopwatch.GetTimestamp();
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, seed);
            double buildMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var bots = new List<BotBody>();
            for (int i = 0; i < 3; i++)
            {
                Entity marker = NetWorld.SlotMarker(world.Builder, NetWorld.BotSlots[i]);
                var bot = new BotBody(director, states, NetWorld.FirstPlayerId + 1 + i);
                Entity e = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation,
                                           bot, new DamagePercent());
                e.Name = $"Bot {i + 1}";
                if (Environment.GetEnvironmentVariable("KOTH_HOPLOG") is string who && (who == "0" || who == $"{i + 1}"))
                    director.BrainOf(bot.NetId).HopLog = line => Log($"[sprung] {line}");
                bots.Add(bot);
            }

            var times = new float[bots.Count];
            var best = new float[bots.Count];
            var low = new bool[bots.Count];
            var angles = new float[bots.Count, Heights.Length];
            int falls = 0, respawns = 0;
            for (int i = 0; i < bots.Count; i++)
            {
                times[i] = -1f;
                best[i] = float.MinValue;
                for (int h = 0; h < Heights.Length; h++)
                    angles[i, h] = float.NaN;
            }

            bool trace = Environment.GetEnvironmentVariable("KOTH_BOTTRACE") != null;
            int traceUntil = int.Parse(Environment.GetEnvironmentVariable("KOTH_BOTTRACE") ?? "0");
            int traceBot = int.Parse(Environment.GetEnvironmentVariable("KOTH_BOTTRACE_BOT") ?? "0");
            int left = bots.Count;
            var before = new MotorState[bots.Count];
            for (int t = 0; t < ClimbTicks && left > 0; t++)
            {
                for (int i = 0; i < bots.Count; i++)
                    before[i] = bots[i].Motor.CaptureState();
                long tick = Stopwatch.GetTimestamp();
                world.Simulation.Tick();
                double us = Stopwatch.GetElapsedTime(tick).TotalMicroseconds;
                ticks.Add(us);
                if (us > 5000)
                    Log($"[klettern] langer Tick {t}: {us:F0} µs");
                for (int i = 0; i < bots.Count; i++)
                {
                    Vector3 p = bots[i].Motor.Position;
                    if (p.Y > best[i])
                        best[i] = p.Y;
                    else if (best[i] - p.Y > 3f && best[i] > 3f)
                    {
                        falls++;
                        best[i] = p.Y;
                    }
                    respawns += low[i] && p.Y > 0.5f ? 1 : 0;
                    low[i] = p.Y < -1f;
                    for (int h = 0; h < Heights.Length; h++)
                        if (float.IsNaN(angles[i, h]) && p.Y > Heights[h])
                            angles[i, h] = MathF.Atan2(p.X, p.Z) * (180f / MathF.PI);
                    if (times[i] < 0f && match.Contains(p))
                    {
                        times[i] = (t + 1) / 40f;
                        left--;
                    }
                    BotBrain mind = director.BrainOf(bots[i].NetId);
                    if (mind.Climbing)
                    {
                        MotorState real = before[i];
                        Vector3 q = real.Position;
                        JumpPilot.Body pb = mind.Predicted;
                        Predictions[0]++;
                        Predictions[1] += pb.Grounded != real.Grounded ? 1 : 0;
                        float ex = pb.X - q.X, ey = pb.Y - (q.Y - 0.9f), ez = pb.Z - q.Z;
                        PredictionError += MathF.Sqrt(ex * ex + ey * ey + ez * ez);
                        int cls = (real.Grounded ? 0 : 2) + (pb.Grounded ? 0 : 1);
                        PredClass[cls]++;
                        PredClassErr[cls] += MathF.Sqrt(ex * ex + ey * ey + ez * ez);
                        PredClassErrY[cls] += MathF.Abs(ey);
                    }
                    if (t % 10 == 0)
                        path?.WriteLine(FormattableString.Invariant($"{seed},{t},{i + 1},{p.X:F2},{p.Y:F2},{p.Z:F2}"));
                    if (trace && t < traceUntil && (traceBot == 0 || traceBot == i + 1))
                    {
                        BotBrain br = director.BrainOf(bots[i].NetId);
                        var m = bots[i].Motor.CaptureState();
                        string next = br.Corners.Count > 1 ? $"{br.Corners[1].Position.X:F1} {br.Corners[1].Position.Y:F1} {br.Corners[1].Position.Z:F1} {br.Corners[1].Link}" : "-";
                        string after = br.Corners.Count > 2 ? $"{br.Corners[2].Position.X:F1} {br.Corners[2].Position.Y:F1} {br.Corners[2].Position.Z:F1} {br.Corners[2].Link}" : "-";
                        Log($"[spur] {t} Bot {i + 1} {br.Intent} {(br.Climbing ? "K" : br.Running.ToString())} {br.Flight} v{br.Variant} fuß {p.X:F2} {p.Y - 0.9f:F2} {p.Z:F2} " +
                            $"tempo {m.Velocity.X:F1} {m.Velocity.Y:F1} {m.Velocity.Z:F1} boden {(m.Grounded ? 1 : 0)} " +
                            $"ecke1 {next} ecke2 {after} feld {director.Navigation.Grid.TimeToZone(new Vector3(p.X, p.Y - 0.9f, p.Z), br.Variant):F1} lehren {br.Lessons} hops {br.Hops}/{br.HopsLanded} weg {string.Join(";", System.Linq.Enumerable.Select(br.Corners, c => FormattableString.Invariant($"{c.Position.X:F2},{c.Position.Y:F2},{c.Position.Z:F2},{c.Link}")))} land {br.Landing.X:F1} {br.Landing.Y:F1} {br.Landing.Z:F1}");
                    }
                }
            }

            float sum = 0f;
            int pairs = 0;
            for (int h = 0; h < Heights.Length; h++)
                for (int a = 0; a < bots.Count; a++)
                    for (int b = a + 1; b < bots.Count; b++)
                    {
                        if (float.IsNaN(angles[a, h]) || float.IsNaN(angles[b, h]))
                            continue;
                        sum += MathF.Abs(MathF.IEEERemainder(angles[a, h] - angles[b, h], 360f));
                        pairs++;
                    }
            spread = pairs > 0 ? sum / pairs : -1f;

            foreach (BotBody b in bots)
            {
                BotBrain mind = director.BrainOf(b.NetId);
                HopStats[0] += mind.Hops;
                HopStats[1] += mind.HopsLanded;
                HopStats[2] += mind.HopsGivenUp;
            }
            var highest = new List<string>();
            for (int i = 0; i < bots.Count; i++)
                highest.Add($"{bots[i].Motor.Position.Y:F0}");
            extra = $"Navigation {buildMs:F0} ms, {falls} Stürze, {respawns} Respawns, Höhe am Ende {string.Join("/", highest)} m";
            return times;
        }
    }
}
