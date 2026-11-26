using System;
using System.Collections.Generic;
using System.Linq;
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
        readonly double[] zoneIntent = new double[8];
        readonly Dictionary<string, int> reversalsBy = new Dictionary<string, int>(), tapsBy = new Dictionary<string, int>();
        readonly int[] keySince = new int[64], wishTick = new int[64];
        readonly Vector2[] lastWish = new Vector2[64];
        readonly Dictionary<string, int> worldBy = new Dictionary<string, int>();
        double taps, worldReversals;
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

            int traceBot = int.Parse(Environment.GetEnvironmentVariable("KOTH_ROUNDS_TRACE") ?? "0") - 1, traced = 0;
            int traceLimit = int.Parse(Environment.GetEnvironmentVariable("KOTH_ROUNDS_TRACE_TICKS") ?? "2400");
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
            bool wasOver = false, wasScoring = false;
            int falls = 0, holds = 0, heldTicks = 0, lastScoreSum = 0, empty = 0;
            long insideSum = 0, returnSum = 0;
            int returns = 0, aloneRun = 0, alones = 0, alonesTaken = 0, aloneEndedBy = 0;
            var fellAt = new long[count];
            var lastScoreOf = new int[count];
            var backSum = new long[count];
            var backN = new int[count];
            int holderIdx = -1;
            var holdEnds = new Dictionary<string, int>();
            int onHolder = 0, onOthers = 0;
            Array.Fill(fellAt, -1L);
            var wasIn = new bool[count];
            var lastHit = new int[count];
            var swingsWas = new int[count];
            var hitsWas = new int[count];
            var missWhy = new long[6];   // no target, far, yaw, pitch, other; hits
            var hitInZoneAt = new long[count];
            long zoneHits = 0, knockedOut = 0, hitFalls = 0;
            double zonePercent = 0, zonePercentN = 0, hitPercent = 0;
            for (int i = 0; i < count; i++)
            {
                hitInZoneAt[i] = -1000;
                lastHit[i] = -1;
            }
            for (long t = 0; done < wanted && t < 52000L * wanted; t++)
            {
                world.Simulation.Tick();
                MatchState m = match.State;
                bool running = m.Phase == MatchPhase.Running && !m.Over;
                if (running && m.RoundTicks == 1)
                {
                    leader = -1;
                    leadChanges = contested = 0;
                    falls = holds = heldTicks = lastScoreSum = empty = 0;
                    insideSum = returnSum = 0;
                    returns = aloneRun = alones = alonesTaken = aloneEndedBy = 0;
                    Array.Fill(fellAt, -1L);
                    wasScoring = false;
                    Array.Clear(wasIn);
                    Array.Clear(up);
                    for (int i = 0; i < count; i++)
                        hitsBefore[i] = Hits(bots[i]);
                }
                for (int i = 0; i < count; i++)
                {
                    var melee = bots[i].Entity.Get<MeleeAttack>();
                    if (melee.SwingCount == swingsWas[i])
                        continue;
                    swingsWas[i] = melee.SwingCount;
                    bool hit = melee.HitCount != hitsWas[i];
                    hitsWas[i] = melee.HitCount;
                    if (hit)
                    {
                        missWhy[5]++;
                        continue;
                    }
                    BotBrain mind = director.BrainOf(bots[i].NetId);
                    int ti = bots.FindIndex(b => b.NetId == mind.Target);
                    if (ti < 0)
                    {
                        missWhy[0]++;
                        continue;
                    }
                    Vector3 to = bots[ti].Motor.BodyCenter - bots[i].Motor.EyePosition;
                    float flat = MathF.Sqrt(to.X * to.X + to.Z * to.Z), dist = to.Length();
                    float yawTo = MathF.Atan2(-to.X, -to.Z) * 180f / MathF.PI, pitchTo = MathF.Atan2(to.Y, flat) * 180f / MathF.PI;
                    float dy = MathF.Abs(MathF.IEEERemainder(yawTo - mind.LastInput.Yaw, 360f)), dp2 = MathF.Abs(pitchTo - mind.LastInput.Pitch);
                    missWhy[dist > 3.8f ? 1 : dy > MathF.Atan2(0.35f, flat) * 180f / MathF.PI ? 2 : dp2 > MathF.Atan2(0.8f, flat) * 180f / MathF.PI ? 3 : 4]++;
                }
                if (running)
                {
                    contested += m.Inside > 1 ? 1 : 0;
                    // Alone spells: how often one stood alone, held it past the warm-up, or was joined.
                    if (m.Inside == 1)
                        aloneRun++;
                    else if (aloneRun > 0)
                    {
                        alones++;
                        alonesTaken += aloneRun > 120 ? 1 : 0;
                        aloneEndedBy += m.Inside > 1 ? 1 : 0;
                        aloneRun = 0;
                    }
                    empty += m.Inside == 0 ? 1 : 0;
                    insideSum += m.Inside;
                    int scoreSum = 0;
                    for (int i = 0; i < count; i++)
                    {
                        scoreSum += bots[i].ScoreTicks;
                        Vector3 at = bots[i].Motor.Position;
                        var dp = bots[i].Entity.Get<DamagePercent>();
                        bool inside = match.Contains(at);
                        if (dp.LastHitTick != lastHit[i])
                        {
                            lastHit[i] = dp.LastHitTick;
                            if (inside && wasScoring && holderIdx >= 0)
                            {
                                onHolder += i == holderIdx ? 1 : 0;
                                onOthers += i == holderIdx ? 0 : 1;
                            }
                            if (inside)
                            {
                                zoneHits++;
                                hitPercent += dp.Percent;
                                hitInZoneAt[i] = t;
                            }
                        }
                        if (inside)
                        {
                            zonePercent += dp.Percent;
                            zonePercentN++;
                        }
                        if (inside)
                        {
                            if (fellAt[i] >= 0)
                            {
                                returnSum += t - fellAt[i];
                                returns++;
                                backSum[i] += t - fellAt[i];
                                backN[i]++;
                                fellAt[i] = -1;
                            }
                            wasIn[i] = true;
                        }
                        else if (wasIn[i] && at.Y < match.Center.Y - match.HalfSize.Y - 8f)
                        {
                            wasIn[i] = false;
                            fellAt[i] = t;
                            falls++;
                            hitFalls += t - hitInZoneAt[i] < 120 ? 1 : 0;
                        }
                        if (!inside && hitInZoneAt[i] >= 0 && t - hitInZoneAt[i] < 40)
                        {
                            knockedOut++;
                            hitInZoneAt[i] = t - 40;   // counted once, still a fall cause
                        }
                    }
                    bool scoring = scoreSum > lastScoreSum;
                    if (scoring)
                        for (int i = 0; i < count; i++)
                            if (bots[i].ScoreTicks > lastScoreOf[i])
                                holderIdx = i;
                    for (int i = 0; i < count; i++)
                        lastScoreOf[i] = bots[i].ScoreTicks;
                    if (!scoring && wasScoring && holderIdx >= 0)
                    {
                        // Why a hold ended: knocked out (hit in the zone within 1.5 s) or left on its own, by intent.
                        bool knocked = t - hitInZoneAt[holderIdx] < 100;
                        string why = knocked ? "knocked" : director.BrainOf(bots[holderIdx].NetId).Intent.ToString();
                        holdEnds[why] = holdEnds.GetValueOrDefault(why) + 1;
                    }
                    holds += scoring && !wasScoring ? 1 : 0;
                    heldTicks += scoring ? 1 : 0;
                    wasScoring = scoring;
                    lastScoreSum = scoreSum;
                    int best = -1;
                    for (int i = 0; i < count; i++)
                        if (bots[i].ScoreTicks >= HillMatch.TicksPerPoint && (best < 0 || bots[i].ScoreTicks > bots[best].ScoreTicks))
                            best = i;
                    if (best >= 0 && best != leader)
                    {
                        leadChanges += leader >= 0 ? 1 : 0;
                        leader = best;
                    }
                    if (traceBot >= 0 && up[traceBot] && traced < traceLimit)
                    {
                        traced++;
                        BotBrain tm = director.BrainOf(bots[traceBot].NetId);
                        MoveInput ti = tm.LastInput;
                        MotorState ts = bots[traceBot].Motor.CaptureState();
                        int tj = bots.FindIndex(b => b.NetId == tm.Target);
                        float td = tj >= 0 ? (bots[tj].Motor.Position - ts.Position).Length() : -1f;
                        Log($"[trace] {t} in {(match.Contains(ts.Position) ? 1 : 0)} {tm.Intent} t{(tj >= 0 ? tj + 1 : 0)} d {td:F1} ph {tm.FightStage} " +
                            $"{tm.Running} keys {ti.Move.X:F0},{ti.Move.Y:F0} s{(ti.Sprint ? 1 : 0)}c{(ti.Crouch ? 1 : 0)}j{ti.JumpPresses}a{ti.AttackPresses} " +
                            $"yaw {ti.Yaw:F0} v {MathF.Sqrt(ts.Velocity.X * ts.Velocity.X + ts.Velocity.Z * ts.Velocity.Z):F1} dmg {bots[traceBot].Entity.Get<DamagePercent>().Percent:F0} " +
                            $"at {ts.Position.X:F1} {ts.Position.Y:F1} {ts.Position.Z:F1} cl {(tm.Climbing ? 1 : 0)} goal {tm.Goal.X:F1} {tm.Goal.Y:F1} {tm.Goal.Z:F1} " +
                            $"corners {string.Join(" ", tm.Corners.Take(4).Select(c => $"{c.Position.X:F1},{c.Position.Y:F1},{c.Position.Z:F1}/{c.Link}"))} hop {(tm.Hopping ? 1 : 0)} fl {tm.Flight}");
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
                    // A team's win counts for each of its members (teams are drawn anew each round).
                    for (int i = 0; i < count && w >= 0; i++)
                        if (i == w || bots[w].Team != 0 && bots[i].Team == bots[w].Team)
                            wins[i]++;
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
                        $"{hits / (seconds / 60.0) / count:F1} hits/min per bot, scored {total * 100.0 / Math.Max(1, m.RoundTicks):F0} % of the time, " +
                        $"falls {falls}, holds {holds} (mean {heldTicks / 40.0 / Math.Max(1, holds):F0} s), " +
                        $"empty {empty * 100.0 / Math.Max(1, m.RoundTicks):F0} %, inside {insideSum / (double)Math.Max(1, m.RoundTicks):F1}, " +
                        $"back after a fall {returnSum / 40.0 / Math.Max(1, returns):F0} s, alone {alones} times ({alonesTaken} over 3 s, {aloneEndedBy} ended by another coming)");
                }
                wasOver = m.Over;
            }
            for (int i = 0; i < count; i++)
            {
                Personality pp = director.BrainOf(bots[i].NetId).Persona;
                Log($"[persona] bot {i + 1}: wins {wins[i]}, aggression {pp.Aggression:F2}, caution {pp.Caution:F2}, accuracy {pp.Accuracy:F2}, " +
                    $"hand {pp.Hand:F2}, blaster {pp.Blaster:F2}, focus {pp.Focus}, back {backSum[i] / 40.0 / Math.Max(1, backN[i]):F1} s, hits {Hits(bots[i])}");
            }
            Log($"[holds] while held, hits on the holder {onHolder}, on others in the zone {onOthers}; ended: {string.Join(", ", holdEnds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}");
            director.Dispose();
            Log($"[rounds] melee swings: hit {missWhy[5]}, missed without target {missWhy[0]}, too far {missWhy[1]}, beside {missWhy[2]}, above/below {missWhy[3]}, other {missWhy[4]}");
            Log($"[rounds] hits in the zone {zoneHits}: out within 1 s {knockedOut * 100 / Math.Max(1, zoneHits)} %, percent at the hit {hitPercent / Math.Max(1, zoneHits):F0}, " +
                $"in the zone {zonePercent / Math.Max(1, zonePercentN):F0}; falls after a hit {hitFalls}");

            int n = Math.Max(1, done);
            int most = 0;
            foreach (int w in wins)
                most = Math.Max(most, w);
            Log($"[rounds] {done} rounds: mean {sumSeconds / n:F0} s, by points {byPoints * 100 / n} %, winner's share {sumShare / n * 100:F0} %, " +
                $"{sumLeads / n:F1} lead changes, contested {sumContested / n * 100:F0} %, {sumHits / n:F1} hits/min per bot");
            Log($"[rounds] wins per bot: {string.Join(" ", wins)} (most {most}, fair {done * HillMatch.Size(match.Mode) / (float)count:F1})");
            double min = Math.Max(1, natTicks) / 2400.0, ground = Math.Max(1, natGround);
            Log($"[rounds] view: {turnSum / Math.Max(1, natTicks):F2}°/tick, {jerks / min:F1} jerks/min (turn rate changed > 8°/tick), " +
                $"{flicks / min:F1} flicks/min (> 5° and straight back)");
            Log($"[rounds] keys: {keyChanges / ground * 40:F2} changes/s, {reversals / ground * 2400:F1} reversals/min on the ground; " +
                $"manoeuvre held {maneuverTicks / Math.Max(1, maneuverChanges) / 40:F2} s; {taps / ground * 2400:F1} one-tick keys/min");
            Log($"[rounds] reversals by: {string.Join(", ", reversalsBy.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value * 2400.0 / ground:F1}"))}");
            Log($"[rounds] way reversals (> 120° in 0.2 s) {worldReversals / ground * 2400:F1}/min: {string.Join(", ", worldBy.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value * 2400.0 / ground:F1}"))}");
            Log($"[rounds] one-tick keys by: {string.Join(", ", tapsBy.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value * 2400.0 / ground:F1}"))}");
            Log($"[rounds] in the zone: {zoneJumps / Math.Max(1, zoneTicks) * 2400:F1} jumps/min, standing {zoneStanding / Math.Max(1, zoneTicks) * 100:F0} %; intents " +
                string.Join(" ", Enum.GetNames<BotIntent>().Select((n, k) => $"{n} {zoneIntent[k] * 100 / Math.Max(1, zoneTicks):F0} %")));
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
                if (move.X != lastMove[i].X || move.Y != lastMove[i].Y)
                {
                    keyChanges++;
                    // A key state held a single tick (25 ms): a thumb is not that fast.
                    if (t - keySince[i] == 1)
                    {
                        string why = mind.Hopping ? "hop" : mind.Climbing ? "climb" : $"{mind.Intent} {mind.Running}";
                        tapsBy[why] = tapsBy.GetValueOrDefault(why) + 1;
                        taps++;
                    }
                    keySince[i] = t;
                }
                lastMove[i] = move;
                // The wished way in the world (keys turned by the view): a turn of more than 120° within 0.2 s.
                if (move != Vector2.Zero)
                {
                    float r = input.Yaw * (MathF.PI / 180f), c = MathF.Cos(r), sn = MathF.Sin(r);
                    var wish = Vector2.Normalize(new Vector2(move.X * c - move.Y * sn, -move.X * sn - move.Y * c));
                    if (t - wishTick[i] <= 8 && Vector2.Dot(wish, lastWish[i]) < -0.5f)
                    {
                        worldReversals++;
                        string why = mind.Hopping ? "hop" : mind.Climbing ? "climb" : $"{mind.Intent} {mind.Running}";
                        worldBy[why] = worldBy.GetValueOrDefault(why) + 1;
                    }
                    lastWish[i] = wish;
                    wishTick[i] = t;
                }
                for (int a = 0; a < 2; a++)
                {
                    int sign = Math.Sign(a == 0 ? input.Move.X : input.Move.Y);
                    if (sign == 0)
                        continue;
                    if (axisSign[i, a] == -sign && t - axisTick[i, a] <= 8)
                    {
                        reversals++;
                        string why = mind.Hopping ? "hop" : mind.Climbing ? "climb" : $"{mind.Intent} {mind.Running}";
                        reversalsBy[why] = reversalsBy.GetValueOrDefault(why) + 1;
                    }
                    axisSign[i, a] = sign;
                    axisTick[i, a] = t;
                }
            }
            if (inZone)
            {
                zoneTicks++;
                zoneIntent[(int)mind.Intent]++;
                zoneJumps += input.JumpPresses > 0 && ms.Grounded ? 1 : 0;
                zoneStanding += MathF.Sqrt(ms.Velocity.X * ms.Velocity.X + ms.Velocity.Z * ms.Velocity.Z) < 0.3f ? 1 : 0;
            }
        }
    }
}
