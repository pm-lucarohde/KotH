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

        // How human the climbing looks, summed over all bots and rounds: ground ticks,
        // key changes, reversals, strafing ticks, standing ticks, air ticks, yaw sum,
        // yaw wobbles, yaw snaps, pitch sum.
        readonly double[] Nat = new double[18];
        double PredictionError;
        float StartSpacing = float.MaxValue, StartFacing;
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
            int first = int.Parse(Environment.GetEnvironmentVariable("KOTH_CLIMB_FIRST") ?? "1");
            for (int k = 1; k <= seeds; k++)
            {
                int seed = 1000 + first - 1 + k;
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
            double ground = Math.Max(1, Nat[0]), all = Math.Max(1, Nat[0] + Nat[5]);
            Log($"[klettern] Am Boden: {Nat[1] / ground * 40:F2} Tastenwechsel/s, {Nat[2] / ground * 2400:F1} Umkehrungen/min, " +
                $"davon beim Sprung {Nat[10] / Math.Max(1, Nat[11]) * 40:F2}/s in {Nat[11] / ground * 100:F0} % der Zeit (Flugsteuerung am Boden {Nat[12] / Math.Max(1, Nat[13]) * 40:F2}/s in {Nat[13] / ground * 100:F0} %), seitwärts {Nat[3] / ground * 100:F0} %, steht {Nat[4] / ground * 100:F0} %; Blick: {Nat[6] / all:F2}°/Tick, " +
                $"{Nat[7] / all * 2400:F1} Richtungswechsel/min, {Nat[8] / all * 2400:F1} Rucke über 15°/min, Nicken Ø {Nat[9] / all:F1}°");
            Log($"[klettern] Hin und her: {Nat[17] / Math.Max(1, Nat[0]) * 2400:F1}/min am Boden; Laufen gegen Blick: über 35° in {Nat[15] / Math.Max(1, Nat[14]) * 100:F0} %, über 80° (seitwärts, rückwärts) in {Nat[16] / Math.Max(1, Nat[14]) * 100:F0} % der Bodenticks in Bewegung");
            Log($"[klettern] Vorhersage: Boden falsch in {Predictions[1] * 100.0 / Math.Max(1, Predictions[0]):F1} % der Ticks, " +
                $"Lage im Mittel {PredictionError * 100.0 / Math.Max(1, Predictions[0]):F1} cm daneben");
            string[] names = { "Boden/Boden", "Boden/Luft", "Luft/Boden", "Luft/Luft" };
            for (int c = 0; c < 4; c++)
                Log($"[klettern]   echt/Modell {names[c]}: {PredClass[c]} Ticks, {PredClassErr[c] * 100 / Math.Max(1, PredClass[c]):F1} cm, davon Höhe {PredClassErrY[c] * 100 / Math.Max(1, PredClass[c]):F1} cm");
            Log($"[klettern] Tick mit 3 Bots: Median {Pick(0.5):F0} µs, 99 % {Pick(0.99):F0} µs, höchstens {Pick(1.0):F0} µs");
            Log($"[klettern] Startplätze: engster Abstand {StartSpacing:F1} m, Blick höchstens {StartFacing:F1}° neben der Zone");
            bool passed = missing == 0 && average <= ClimbGoal && StartSpacing >= 6f && StartFacing < 1f;
            Log(passed ? "[klettern] BESTANDEN" : "[klettern] NICHT BESTANDEN");
            return passed ? 0 : 1;
        }

        // One tick of one climbing bot into Nat: keys on the ground, the view always.
        void Naturalness(int i, int t, MoveInput input, MotorState m, Vector2[] lastMove, float[] lastYaw, float[] lastTurn,
                         int[,] axisSign, int[,] axisTick, bool hop, bool flying, Vector2[,] flatHistory, int[] lastBack)
        {
            if (m.Grounded)
            {
                Nat[0]++;
                if (input.Move != lastMove[i])
                    Nat[1]++;
                if (input.Move != lastMove[i] && hop)
                    Nat[10]++;
                Nat[11] += hop ? 1 : 0;
                if (input.Move != lastMove[i] && flying)
                    Nat[12]++;
                Nat[13] += flying ? 1 : 0;
                for (int a = 0; a < 2; a++)
                {
                    int sign = Math.Sign(a == 0 ? input.Move.X : input.Move.Y);
                    if (sign == 0)
                        continue;
                    if (axisSign[i, a] == -sign && t - axisTick[i, a] <= 8)
                        Nat[2]++;
                    axisSign[i, a] = sign;
                    axisTick[i, a] = t;
                }
                Nat[3] += input.Move.X != 0f ? 1 : 0;
                float flat = MathF.Sqrt(m.Velocity.X * m.Velocity.X + m.Velocity.Z * m.Velocity.Z);
                Nat[4] += flat < 0.3f ? 1 : 0;
                // Visible back and forth: the way turned round within 0,15 s.
                var v = new Vector2(m.Velocity.X, m.Velocity.Z);
                Vector2 then = flatHistory[i, t % 8];
                if (v.Length() > 0.6f && then.Length() > 0.6f && Vector2.Dot(v, then) < -0.3f * v.Length() * then.Length() && t - lastBack[i] >= 6)
                {
                    Nat[17]++;
                    lastBack[i] = t;
                }
                flatHistory[i, (t + 2) % 8] = v;
                if (flat > 1f)
                {
                    // What the walk clips show: the way against where the body faces.
                    float off = MathF.Abs(MathF.IEEERemainder(MathF.Atan2(-m.Velocity.X, -m.Velocity.Z) * (180f / MathF.PI) - input.Yaw, 360f));
                    Nat[14]++;
                    Nat[15] += off > 35f ? 1 : 0;
                    Nat[16] += off > 80f ? 1 : 0;
                }
            }
            else
                Nat[5]++;
            lastMove[i] = input.Move;
            float turn = MathF.IEEERemainder(input.Yaw - lastYaw[i], 360f);
            Nat[6] += MathF.Abs(turn);
            if (MathF.Abs(turn) > 0.3f && MathF.Abs(lastTurn[i]) > 0.3f && MathF.Sign(turn) != MathF.Sign(lastTurn[i]))
                Nat[7]++;
            Nat[8] += MathF.Abs(turn) > 15f ? 1 : 0;
            Nat[9] += MathF.Abs(input.Pitch);
            lastYaw[i] = input.Yaw;
            lastTurn[i] = turn;
        }

        // A body jammed on steep ground: does any input get it out? Each
        // trial starts from the same motor state; the bots stand still.
        void JamTrials(HeadlessWorld world, BotBody bot, MotorState jammed)
        {
            Log(FormattableString.Invariant($"[klemmt] bei {jammed.Position.X:F2} {jammed.Position.Y:F2} {jammed.Position.Z:F2}, Tempo {jammed.Velocity.X:F2} {jammed.Velocity.Y:F2} {jammed.Velocity.Z:F2}, Normale {jammed.GroundNormal.X:F2} {jammed.GroundNormal.Y:F2} {jammed.GroundNormal.Z:F2}"));
            string[] names = { "nichts", "Sprung", "Doppelsprung", "Ducken", "Ducken + Sprung" };
            for (int mode = 0; mode < names.Length; mode++)
                for (int d = -1; d < 8; d++)
                {
                    bot.Motor.RestoreState(jammed);
                    bot.Motor.Teleport(jammed.Position);
                    for (int t = 0; t < 80; t++)
                    {
                        var input = new MoveInput { Yaw = d * 45f, Move = d < 0 ? Vector2.Zero : new Vector2(0f, 1f) };
                        if (mode == 1 || mode == 4) { input.JumpPresses = (byte)(t % 8 == 0 ? 1 : 0); input.JumpHeld = true; }
                        if (mode == 2) input.JumpPresses = (byte)(t % 8 == 0 ? 3 : 0);
                        if (mode >= 3) input.Crouch = true;
                        bot.Motor.Simulate(input, KotH.Shared.Simulation.SimulationSystem.TickSeconds);
                        bot.Motor.Publish();
                        bot.Motor.SyncHull();
                    }
                    Vector3 p = bot.Motor.Position;
                    float moved = (p - jammed.Position).Length();
                    if (moved > 0.3f)
                        Log(FormattableString.Invariant($"[klemmt]   frei mit {names[mode]}, Richtung {(d < 0 ? "keine" : $"{d * 45}°")}: {moved:F2} m weiter, y {p.Y:F2}"));
                }
            var queries = bot.Motor.World;
            foreach (float up in new[] { 0.1f, 0.4f, 0.9f, 1.5f })
            {
                var parts = new List<string>();
                for (int d = 0; d < 8; d++)
                {
                    float a = d * MathF.PI / 4f;
                    var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
                    var from = new Vector3(jammed.Position.X, jammed.Position.Y - 0.9f + up, jammed.Position.Z);
                    parts.Add(queries.Raycast(from, dir, 3f, KotH.Shared.Physics.WorldQueries.WorldMask, out var hit)
                        ? FormattableString.Invariant($"{d * 45}°:{hit.Distance:F2}({hit.Normal.X:F1},{hit.Normal.Y:F1},{hit.Normal.Z:F1})")
                        : $"{d * 45}°:-");
                }
                Log(FormattableString.Invariant($"[klemmt] Fuß+{up:F1}: {string.Join(" ", parts)}"));
            }
            var below = new Vector3(jammed.Position.X, jammed.Position.Y, jammed.Position.Z);
            if (queries.Raycast(below, new Vector3(0f, -1f, 0f), 5f, KotH.Shared.Physics.WorldQueries.WorldMask, out var down))
                Log(FormattableString.Invariant($"[klemmt] unten {down.Distance:F2} ({down.Normal.X:F2},{down.Normal.Y:F2},{down.Normal.Z:F2})"));
            Log("[klemmt] Versuche fertig");
            bot.Motor.RestoreState(jammed);
            bot.Motor.Teleport(jammed.Position);
        }

        // One round; times in seconds, -1 for a bot that never got up. The
        // spread is the mean angle round the mountain between two bots where
        // they first passed the heights above, -1 when none were passed.
        float[] ClimbRound(int seed, StreamWriter path, List<double> ticks, out float spread, out string extra)
        {
            using var world = new HeadlessWorld();
            world.LoadArena(Maps.Default);
            HillMatch match = world.StartMatch();
            match.Seed = (uint)seed;
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
            bool jamTest = Environment.GetEnvironmentVariable("KOTH_JAMTEST") != null, jamDone = false;
            var jamFor = new int[bots.Count];
            var lastMove = new Vector2[bots.Count];
            var lastYaw = new float[bots.Count];
            var lastTurn = new float[bots.Count];
            var axisSign = new int[bots.Count, 2];
            var axisTick = new int[bots.Count, 2];
            var flatHistory = new Vector2[bots.Count, 8];
            var lastBack = new int[bots.Count];
            var starts = new List<MotorState>();
            var faced = new List<float>();
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
                    if (jamTest && !jamDone && t > 400)
                    {
                        MotorState js = bots[i].Motor.CaptureState();
                        jamFor[i] = js.Wedged && js.OnSteepGround && !js.Grounded ? jamFor[i] + 1 : 0;
                        if (jamFor[i] > 200)
                        {
                            JamTrials(world, bots[i], js);
                            jamDone = true;
                        }
                    }
                    BotBrain mind = director.BrainOf(bots[i].NetId);
                    if (mind.Climbing && times[i] < 0f && t > 0)
                        Naturalness(i, t, mind.LastInput, bots[i].Motor.CaptureState(), lastMove, lastYaw, lastTurn, axisSign, axisTick, mind.Hopping, mind.Flight != LinkKind.None, flatHistory, lastBack);
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
                    if (t == 0)
                    {
                        starts.Add(bots[i].Motor.CaptureState());
                        faced.Add(bots[i].PlacedTick == 0 ? bots[i].PlacedYaw : float.NaN);
                    }
                    if (t % 10 == 0)
                        path?.WriteLine(FormattableString.Invariant($"{seed},{t},{i + 1},{p.X:F2},{p.Y:F2},{p.Z:F2}"));
                    if (trace && t < traceUntil && (traceBot == 0 || traceBot == i + 1))
                    {
                        BotBrain br = director.BrainOf(bots[i].NetId);
                        var m = bots[i].Motor.CaptureState();
                        string next = br.Corners.Count > 1 ? $"{br.Corners[1].Position.X:F1} {br.Corners[1].Position.Y:F1} {br.Corners[1].Position.Z:F1} {br.Corners[1].Link}" : "-";
                        string after = br.Corners.Count > 2 ? $"{br.Corners[2].Position.X:F1} {br.Corners[2].Position.Y:F1} {br.Corners[2].Position.Z:F1} {br.Corners[2].Link}" : "-";
                        Log($"[spur] {t} Bot {i + 1} {br.Intent} {(br.Climbing ? "K" : br.Running.ToString())} {br.Flight} v{br.Variant} tasten {br.LastInput.Move.X:F0},{br.LastInput.Move.Y:F0} gier {br.LastInput.Yaw:F0} fuß {p.X:F2} {p.Y - 0.9f:F2} {p.Z:F2} " +
                            $"tempo {m.Velocity.X:F1} {m.Velocity.Y:F1} {m.Velocity.Z:F1} boden {(m.Grounded ? 1 : 0)}{(m.OnSteepGround ? " steil" : "")}{(m.TouchingGround ? " berührt" : "")}{(m.Wedged ? " klemmt" : "")} n {m.GroundNormal.X:F2} {m.GroundNormal.Y:F2} {m.GroundNormal.Z:F2} " +
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
            // Where the round put them: round the hill, apart, facing it.
            var where = new List<string>();
            float closest = float.MaxValue, worstFacing = 0f;
            for (int a = 0; a < starts.Count; a++)
            {
                Vector3 p = starts[a].Position;
                where.Add($"{MathF.Atan2(p.Z, p.X) * (180f / MathF.PI):F0}°/{MathF.Sqrt(p.X * p.X + p.Z * p.Z):F0} m");
                float facing = float.IsNaN(faced[a]) ? 999f : faced[a];
                float want = MathF.Atan2(p.X, p.Z) * (180f / MathF.PI);
                worstFacing = MathF.Max(worstFacing, MathF.Abs(MathF.IEEERemainder(facing - want, 360f)));
                for (int b = a + 1; b < starts.Count; b++)
                {
                    Vector3 q = starts[b].Position;
                    closest = MathF.Min(closest, MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z)));
                }
            }
            StartSpacing = MathF.Min(StartSpacing, closest);
            StartFacing = MathF.Max(StartFacing, worstFacing);
            extra = $"Start {string.Join(", ", where)}; Navigation {buildMs:F0} ms, {falls} Stürze, {respawns} Respawns, Höhe am Ende {string.Join("/", highest)} m";
            return times;
        }
    }
}
