using System;
using System.Collections.Generic;
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
        /// <summary>Rounds for "bots rounds N": whole rounds of bots only, for balance and how human they look.
        /// KOTH_ROUNDS_MAP, _MODE, _BOTS (8), _SEED (1). Information, no pass mark.</summary>
        public int Rounds = 0;

        // Over all rounds, per bot tick out of the climb (after its first step into the zone): view and keys.
        double natTicks, natGround, jerks, flicks, keyChanges, reversals, zoneTicks, zoneJumps, zoneStanding, turnSum;
        double maneuverChanges, maneuverTicks;

        int RoundsRun(int wanted)
        {
            string map = Environment.GetEnvironmentVariable("KOTH_ROUNDS_MAP") ?? Maps.Default;
            int count = int.Parse(Environment.GetEnvironmentVariable("KOTH_ROUNDS_BOTS") ?? "8");
            int seed = int.Parse(Environment.GetEnvironmentVariable("KOTH_ROUNDS_SEED") ?? "1");
            using var world = new HeadlessWorld();
            world.LoadArena(map);
            HillMatch match = world.StartMatch();
            match.Seed = (uint)seed;
            match.Mode = HillMatch.ParseMode(Environment.GetEnvironmentVariable("KOTH_ROUNDS_MODE")) ?? MatchMode.Solo;
            var states = new LocalFinalStates(world.Simulation);
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, seed);
            var bots = new List<BotBody>();
            for (int i = 0; i < count; i++)
            {
                Entity marker = NetWorld.SlotMarker(world.Builder, NetWorld.BotSlots[i]);
                var bot = new BotBody(director, states, NetWorld.FirstPlayerId + 1 + i);
                Entity e = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation,
                                           bot, new DamagePercent());
                e.Name = $"Bot {i + 1}";
                bots.Add(bot);
            }
            Log($"[rounds] map {map}, mode {match.Mode}, {count} bots, seed {seed}");

            var wins = new int[count];
            var up = new bool[count];
            var lastTurn = new float[count];
            var lastYaw = new float[count];
            var lastMove = new Vector2[count];
            var axisSign = new int[count, 2];
            var axisTick = new int[count, 2];
            var lastManeuver = new Maneuver[count];
            var hitsBefore = new int[count];
            int done = 0, leader = -1, leadChanges = 0, contested = 0, byPoints = 0;
            double sumSeconds = 0, sumShare = 0, sumLeads = 0, sumContested = 0, sumHits = 0;
            bool wasOver = false;
            for (long t = 0; done < wanted && t < 48000L * wanted; t++)
            {
                world.Simulation.Tick();
                MatchState m = match.State;
                bool running = m.Phase == MatchPhase.Running && !m.Over;
                if (running && m.RoundTicks == 1)
                {
                    leader = -1;
                    leadChanges = contested = 0;
                    Array.Clear(up);
                    for (int i = 0; i < count; i++)
                        hitsBefore[i] = Hits(bots[i]);
                }
                if (running)
                {
                    contested += m.Inside > 1 ? 1 : 0;
                    int best = -1;
                    for (int i = 0; i < count; i++)
                        if (bots[i].ScoreTicks >= HillMatch.TicksPerPoint && (best < 0 || bots[i].ScoreTicks > bots[best].ScoreTicks))
                            best = i;
                    if (best >= 0 && best != leader)
                    {
                        leadChanges += leader >= 0 ? 1 : 0;
                        leader = best;
                    }
                    for (int i = 0; i < count; i++)
                        Natural(i, (int)t, director.BrainOf(bots[i].NetId), bots[i], match, up, lastTurn, lastYaw, lastMove,
                                axisSign, axisTick, lastManeuver);
                }
                if (m.Over && !wasOver)
                {
                    done++;
                    int total = 0, hits = 0;
                    for (int i = 0; i < count; i++)
                    {
                        total += bots[i].ScoreTicks;
                        hits += Hits(bots[i]) - hitsBefore[i];
                    }
                    int w = m.Winner != null ? bots.FindIndex(b => b.Entity == m.Winner.Entity) : -1;
                    if (w >= 0)
                        wins[w]++;
                    bool points = m.Winner != null && m.Winner.ScoreTicks >= match.WinTicks;
                    byPoints += points ? 1 : 0;
                    float seconds = m.RoundTicks / 40f;
                    float share = w >= 0 && total > 0 ? bots[w].ScoreTicks / (float)total : 0f;
                    sumSeconds += seconds;
                    sumShare += share;
                    sumLeads += leadChanges;
                    sumContested += contested / (double)Math.Max(1, m.RoundTicks);
                    sumHits += hits / (seconds / 60.0) / count;
                    Log($"[round] {done}: {seconds:F0} s, winner {(w >= 0 ? bots[w].Entity.Name : "none")} by {(points ? "points" : "time")}, " +
                        $"its share {share * 100:F0} %, {leadChanges} lead changes, contested {contested * 100.0 / Math.Max(1, m.RoundTicks):F0} %, " +
                        $"{hits / (seconds / 60.0) / count:F1} hits/min per bot");
                }
                wasOver = m.Over;
            }
            director.Dispose();

            int n = Math.Max(1, done);
            int most = 0;
            foreach (int w in wins)
                most = Math.Max(most, w);
            Log($"[rounds] {done} rounds: mean {sumSeconds / n:F0} s, by points {byPoints * 100 / n} %, winner's share {sumShare / n * 100:F0} %, " +
                $"{sumLeads / n:F1} lead changes, contested {sumContested / n * 100:F0} %, {sumHits / n:F1} hits/min per bot");
            Log($"[rounds] wins per bot: {string.Join(" ", wins)} (most {most}, fair {done / (float)count:F1})");
            double min = Math.Max(1, natTicks) / 2400.0, ground = Math.Max(1, natGround);
            Log($"[rounds] view: {turnSum / Math.Max(1, natTicks):F2}°/tick, {jerks / min:F1} jerks/min (turn rate changed > 8°/tick), " +
                $"{flicks / min:F1} flicks/min (> 5° and straight back)");
            Log($"[rounds] keys: {keyChanges / ground * 40:F2} changes/s, {reversals / ground * 2400:F1} reversals/min on the ground; " +
                $"manoeuvre held {maneuverTicks / Math.Max(1, maneuverChanges) / 40:F2} s");
            Log($"[rounds] in the zone: {zoneJumps / Math.Max(1, zoneTicks) * 2400:F1} jumps/min, standing {zoneStanding / Math.Max(1, zoneTicks) * 100:F0} %");
            return 0;
        }

        static int Hits(BotBody b) => b.Entity.Get<MeleeAttack>().HitCount + b.Entity.Get<Blaster>().HitCount + b.Entity.Get<Dash>().HitCount;

        // One bot tick into the counters, once it has been up in the zone this round (the climb is "bots climb"'s).
        void Natural(int i, int t, BotBrain mind, BotBody bot, HillMatch match, bool[] up, float[] lastTurn, float[] lastYaw,
                     Vector2[] lastMove, int[,] axisSign, int[,] axisTick, Maneuver[] lastManeuver)
        {
            MotorState ms = bot.Motor.CaptureState();
            bool inZone = match.Contains(ms.Position);
            MoveInput input = mind.LastInput;
            float turn = MathF.IEEERemainder(input.Yaw - lastYaw[i], 360f);
            if (!up[i])
            {
                up[i] = inZone;
                lastYaw[i] = input.Yaw;
                lastTurn[i] = turn;
                return;
            }
            natTicks++;
            turnSum += MathF.Abs(turn);
            jerks += MathF.Abs(turn - lastTurn[i]) > 8f ? 1 : 0;
            flicks += MathF.Abs(turn) > 5f && MathF.Abs(lastTurn[i]) > 5f && MathF.Sign(turn) != MathF.Sign(lastTurn[i]) ? 1 : 0;
            lastYaw[i] = input.Yaw;
            lastTurn[i] = turn;
            maneuverTicks++;
            if (mind.Running != lastManeuver[i])
            {
                maneuverChanges++;
                lastManeuver[i] = mind.Running;
            }
            if (ms.Grounded)
            {
                natGround++;
                var move = new Vector2(input.Move.X, input.Move.Y);
                keyChanges += move.X != lastMove[i].X || move.Y != lastMove[i].Y ? 1 : 0;
                lastMove[i] = move;
                for (int a = 0; a < 2; a++)
                {
                    int sign = Math.Sign(a == 0 ? input.Move.X : input.Move.Y);
                    if (sign == 0)
                        continue;
                    if (axisSign[i, a] == -sign && t - axisTick[i, a] <= 8)
                        reversals++;
                    axisSign[i, a] = sign;
                    axisTick[i, a] = t;
                }
            }
            if (inZone)
            {
                zoneTicks++;
                zoneJumps += input.JumpPresses > 0 && ms.Grounded ? 1 : 0;
                zoneStanding += MathF.Sqrt(ms.Velocity.X * ms.Velocity.X + ms.Velocity.Z * ms.Velocity.Z) < 0.3f ? 1 : 0;
            }
        }
    }
}
