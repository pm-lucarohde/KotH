using System;
using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Shared.Match
{
    /// <summary>A body's part in "1, 2, 3, Free". None outside the mode; Caught, Free and Waiting are out
    /// of the round (SimulationBody.IsOut). Rescuing: the last runner, free already, counting on to 6 s.</summary>
    public enum FreeRole : byte { None, Runner, Catcher, Caught, Free, Waiting, Rescuing }

    /// <summary>"1, 2, 3, Free" (Luca, 25.11.2026): a drawn catcher against the runners. Runners get free
    /// alone 3 s in the zone without a catcher in it; the catcher's hit catches. The rules in order are in
    /// the memory (koth-game-modes); everything is state of the bodies and the round, rolled back alike.</summary>
    public sealed partial class HillMatch
    {
        public const int FreeMinPlayers = 5, FreeMaxPlayers = 16, NormalMaxPlayers = 12, TwoCatchersFrom = 11;

        /// <summary>The draw's wheel (all stand), the runners' wait (the catcher's head start), the counts.</summary>
        public static readonly int DrawTicks = Ticks(5f), HeadStartTicks = Ticks(15f);
        public static readonly int FreeTicks = Ticks(3f), RescueTicks = Ticks(6f), MarkTicks = Ticks(3f);
        static readonly int FreeLimitTicks = Ticks(15f * 60f);

        /// <summary>In the head start the catcher stays this far from every runner, flat metres.</summary>
        public const float KeepAway = 15f;

        /// <summary>Odds: certain to be drawn again (a catcher who caught nobody).</summary>
        public const byte Certain = 255;

        /// <summary>Most players a game of the mode takes.</summary>
        public static int MaxPlayers(MatchMode mode) => mode == MatchMode.Free ? FreeMaxPlayers : NormalMaxPlayers;

        /// <summary>Whether the round is one of 1, 2, 3, Free.</summary>
        public bool IsFree => state.Mode == MatchMode.Free;

        /// <summary>The round's length in ticks: 15 minutes in 1, 2, 3, Free.</summary>
        public int RoundLimitTicks => IsFree ? FreeLimitTicks : LimitTicks;

        bool FreeRunning => IsFree && state.Phase == MatchPhase.Running;

        /// <summary>The wheel turns: nobody moves (the first DrawTicks of a round).</summary>
        public bool Drawing => FreeRunning && state.RoundTicks < DrawTicks;

        /// <summary>The catcher's head start: the runners stand and cannot be caught.</summary>
        public bool HeadStart => FreeRunning && state.RoundTicks >= DrawTicks && state.RoundTicks < DrawTicks + HeadStartTicks;

        /// <summary>Ticks left of the head start, 0 once the round plays.</summary>
        public int HeadStartLeft => FreeRunning ? Math.Max(0, DrawTicks + HeadStartTicks - state.RoundTicks) : 0;

        /// <summary>Whether catching and counting go on: after the draw and the head start.</summary>
        public bool Playing => FreeRunning && state.RoundTicks >= DrawTicks + HeadStartTicks;

        /// <summary>Runners left before a hit only marks: 2, or 4 with two catchers.</summary>
        int MarkFrom => 2 * Math.Max(1, (int)state.Catchers);

        readonly List<SimulationBody> order = new List<SimulationBody>(NetWorld.MaxPlayers + 4);
        readonly List<SimulationBody> pool = new List<SimulationBody>(NetWorld.MaxPlayers + 4);
        readonly List<SimulationSystem.HitReport> hitOrder = new List<SimulationSystem.HitReport>(64);
        float placeRoom;

        /// <summary>What a body may do this tick: while the wheel turns nobody moves, in the head start the
        /// runners stand (the view still turns) and the catcher does not attack. Called by SimulationSystem.</summary>
        public MoveInput Filter(SimulationBody body, in MoveInput input)
        {
            if (!FreeRunning || body.IsOut || body.NetId < NetWorld.FirstPlayerId || state.RoundTicks >= DrawTicks + HeadStartTicks)
                return input;
            if (state.RoundTicks < DrawTicks || body.Role != FreeRole.Catcher)
                return new MoveInput { Yaw = input.Yaw, Pitch = input.Pitch, Blaster = input.Blaster };
            MoveInput held = input;
            held.AttackPresses = 0;
            if (!body.Motor.IsGrounded)
                held.CrouchPressed = false;   // no dash; a slide on the ground stays
            return held;
        }

        /// <summary>The head start's barrier, after the bodies moved: a catcher closer than KeepAway to a runner
        /// is pushed back out (swept), flat. Called by SimulationSystem.</summary>
        public void Hold(IReadOnlyList<SimulationBody> bodies)
        {
            if (!FreeRunning || state.RoundTicks >= DrawTicks + HeadStartTicks)
                return;
            Sorted(bodies);
            foreach (SimulationBody catcher in order)
            {
                if (catcher.Role != FreeRole.Catcher)
                    continue;
                foreach (SimulationBody runner in order)
                {
                    if (runner.Role != FreeRole.Runner)
                        continue;
                    Vector3 c = catcher.Motor.Position, r = runner.Motor.Position;
                    float dx = c.X - r.X, dz = c.Z - r.Z, d = UMath.Sqrt(dx * dx + dz * dz);
                    if (d >= KeepAway)
                        continue;
                    if (d < 1e-3f)
                        (dx, dz, d) = (1f, 0f, 1f);
                    float out_ = (KeepAway - d) / d;
                    catcher.Motor.Nudge(new Vector3(dx * out_, 0f, dz * out_));
                }
            }
        }

        /// <summary>Whether the body won the round just over: free, or a catcher with one of its catches still caught.</summary>
        public static bool Won(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
        {
            if (body.Role is FreeRole.Free or FreeRole.Rescuing)
                return true;
            if (body.Role != FreeRole.Catcher)
                return false;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Role == FreeRole.Caught && bodies[i].MarkedBy == body.NetId)
                    return true;
            return false;
        }

        /// <summary>Whether a catcher stands in the zone (for the HUD and the zone's colour).</summary>
        public bool CatcherIn(IReadOnlyList<SimulationBody> bodies)
        {
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Role == FreeRole.Catcher && Contains(bodies[i].Motor.Position))
                    return true;
            return false;
        }

        /// <summary>Runners still to get free or be caught (the last one rescuing among them).</summary>
        public static int RunnersLeft(IReadOnlyList<SimulationBody> bodies)
        {
            int n = 0;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Role is FreeRole.Runner or FreeRole.Rescuing)
                    n++;
            return n;
        }

        // Joined into a running round: watches until the next.
        void Await()
        {
            foreach (SimulationBody body in moving)
                if (body.NetId >= NetWorld.FirstPlayerId)
                    body.Role = FreeRole.Waiting;
        }

        void TickFree(IReadOnlyList<SimulationBody> bodies)
        {
            Sorted(bodies);
            // Nobody to draw yet: a host's round from the lobby waits at its start for a person, as its bots are in
            // before the host's own player (it was a spectator of its own round); elsewhere for any player.
            if (state.RoundTicks == 0 && (Stage == MatchStage.Round ? CountPeople(bodies) == 0
                                          : order.Count == 0 || order[^1].NetId < NetWorld.FirstPlayerId))
                return;
            if (state.RoundTicks == 0)
                Draw(bodies, true);
            else if (Count(FreeRole.Catcher) < state.Catchers && Count(FreeRole.Runner) > 0)
                Draw(bodies, false);   // a catcher left: another at once, nobody waits

            bool playing = state.RoundTicks >= DrawTicks + HeadStartTicks;
            if (playing)
                Catches(bodies);

            // Who stands in the zone, by NetId; a catcher inside stops every runner's count.
            bool catcherIn = false;
            int inside = 0;
            foreach (SimulationBody body in order)
            {
                if (body.IsOut || body.Role == FreeRole.None)
                    continue;
                bool isIn = Contains(body.Motor.Position);
                inside += isIn ? 1 : 0;
                catcherIn |= isIn && body.Role == FreeRole.Catcher;
            }
            state.Inside = inside;
            state.Holder = null;

            if (playing)
                foreach (SimulationBody body in order)
                {
                    bool isIn = !body.IsOut && Contains(body.Motor.Position);
                    if (body.Role == FreeRole.Catcher)
                    {
                        body.RoleTicks = isIn ? body.RoleTicks + 1 : 0;
                        if (body.RoleTicks >= MarkTicks)
                            CatchMarked(body);
                    }
                    else if (body.Role == FreeRole.Runner)
                    {
                        body.RoleTicks = isIn && !catcherIn ? body.RoleTicks + 1 : 0;
                        if (body.RoleTicks >= FreeTicks)
                            GetFree(body);
                    }
                    else if (body.Role == FreeRole.Rescuing)
                    {
                        if (!isIn)
                            body.Role = FreeRole.Free;    // left before 6 s: free alone
                        else
                        {
                            body.RoleTicks = catcherIn ? 0 : body.RoleTicks + 1;
                            if (body.RoleTicks >= RescueTicks)
                                Rescue(body);
                        }
                    }
                }

            state.RoundTicks++;
            if (playing && RunnersLeft(bodies) == 0 || state.RoundTicks >= FreeLimitTicks)
                EndFree(bodies);
        }

        // The catchers' hits this tick, by target then catcher (the list's order stays out): a runner is
        // caught, or with few runners left marked; each hit on a runner brings the catcher a shot back.
        void Catches(IReadOnlyList<SimulationBody> bodies)
        {
            IReadOnlyList<SimulationSystem.HitReport> hits = bodies.Count > 0 ? bodies[0].Simulation?.Hits : null;
            if (hits == null || hits.Count == 0)
                return;
            hitOrder.Clear();
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].By.Role == FreeRole.Catcher && hits[i].Target.Role == FreeRole.Runner)
                    hitOrder.Add(hits[i]);
            for (int i = 1; i < hitOrder.Count; i++)
                for (int j = i; j > 0 && Before(hitOrder[j], hitOrder[j - 1]); j--)
                    (hitOrder[j - 1], hitOrder[j]) = (hitOrder[j], hitOrder[j - 1]);
            foreach (SimulationSystem.HitReport hit in hitOrder)
            {
                hit.By.RefundShot();
                if (hit.Target.Role != FreeRole.Runner)
                    continue;   // caught by another hit this tick
                if (Count(FreeRole.Runner) <= MarkFrom)
                {
                    hit.Target.MarkedBy = hit.By.NetId;
                    hit.By.RoleTicks = 0;   // its 3 s in the zone count from the mark
                }
                else
                    Catch(hit.Target, hit.By);
            }
        }

        static bool Before(in SimulationSystem.HitReport a, in SimulationSystem.HitReport b)
            => a.Target.NetId != b.Target.NetId ? a.Target.NetId < b.Target.NetId : a.By.NetId < b.By.NetId;

        void Catch(SimulationBody runner, SimulationBody catcher)
        {
            int last = 0;
            foreach (SimulationBody b in order)
                last = Math.Max(last, b.CaughtOrder);
            runner.Role = FreeRole.Caught;
            runner.CaughtOrder = last + 1;
            runner.MarkedBy = catcher.NetId;
            runner.RoleTicks = 0;
        }

        // The catcher 3 s in the zone since its mark: its marked runners are caught.
        void CatchMarked(SimulationBody catcher)
        {
            foreach (SimulationBody b in order)
                if (b.Role == FreeRole.Runner && b.MarkedBy == catcher.NetId)
                    Catch(b, catcher);
        }

        // 3 s: free; the last runner with someone caught counts on to rescue them (Luca).
        void GetFree(SimulationBody runner)
        {
            runner.MarkedBy = 0;
            if (Count(FreeRole.Runner) == 1 && Count(FreeRole.Caught) > 0)
                runner.Role = FreeRole.Rescuing;
            else
            {
                runner.Role = FreeRole.Free;
                runner.RoleTicks = 0;
            }
        }

        // 6 s alone: every caught one is free, the catches undone (so the catcher counts as having none).
        void Rescue(SimulationBody last)
        {
            foreach (SimulationBody b in order)
                if (b.Role == FreeRole.Caught)
                {
                    b.Role = FreeRole.Free;
                    b.CaughtOrder = 0;
                    b.MarkedBy = 0;
                }
            last.Role = FreeRole.Free;
        }

        /// <summary>The draw: at the round's start everyone a runner, then the catchers by the Odds (certain ones
        /// first, else weighted, all 0 alike), away from the others; later, when one left, alike among the runners.
        /// Dice from the game's seed and the tick, bodies by NetId: every peer draws the same.</summary>
        void Draw(IReadOnlyList<SimulationBody> bodies, bool start)
        {
            if (start)
            {
                int players = 0;
                foreach (SimulationBody b in order)
                    if (b.NetId >= NetWorld.FirstPlayerId)
                    {
                        b.Role = FreeRole.Runner;
                        b.RoleTicks = b.CaughtOrder = b.MarkedBy = 0;
                        b.Damage?.Restore(UMath.Max(b.Damage.Percent, StartPercent));
                        players++;
                    }
                state.Catchers = (byte)(players >= TwoCatchersFrom ? 2 : 1);
            }
            int tick = bodies[0].Simulation?.CurrentTick ?? 0;
            uint dice = Mix(state.Seed ^ Mix((uint)tick * 2246822519u ^ 0x51ED270Bu));
            for (int need = state.Catchers - Count(FreeRole.Catcher); need > 0; need--)
            {
                SimulationBody chosen = Pick(start, ref dice);
                if (chosen == null)
                    break;
                chosen.Role = FreeRole.Catcher;
                chosen.RoleTicks = 0;
                chosen.MarkedBy = 0;
                if (start && HasSpawnRing)
                {
                    moving.Clear();
                    moving.Add(chosen);
                    placeRoom = KeepAway;
                    Place(bodies);
                    placeRoom = 0f;
                }
            }
            state.DrawnAt = state.RoundTicks;
        }

        // One runner by the weights (see Draw); null with none left.
        SimulationBody Pick(bool weighted, ref uint dice)
        {
            pool.Clear();
            bool certain = false;
            foreach (SimulationBody b in order)
                if (b.Role == FreeRole.Runner)
                {
                    pool.Add(b);
                    certain |= weighted && b.Odds == Certain;
                }
            if (pool.Count == 0)
                return null;
            int total = 0;
            foreach (SimulationBody b in pool)
                total += Weight(b, weighted, certain);
            if (total == 0)
                return pool[Math.Min(pool.Count - 1, (int)(Unit(ref dice) * pool.Count))];
            int at = Math.Min(total - 1, (int)(Unit(ref dice) * total));
            foreach (SimulationBody b in pool)
            {
                at -= Weight(b, weighted, certain);
                if (at < 0)
                    return b;
            }
            return pool[pool.Count - 1];
        }

        /// <summary>A runner's weight in a draw: alike unless weighted (the round's start), then only the certain
        /// ones while there is one, else the Odds. All 0: alike (Pick).</summary>
        public static int Weight(SimulationBody b, bool weighted, bool certain)
            => !weighted ? 1 : certain ? b.Odds == Certain ? 1 : 0 : b.Odds;

        /// <summary>The wheel's share of a body in the draw at the round's start, 0 to 1 (the HUD shows it as the slice).</summary>
        public static float Share(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
        {
            if (body.NetId < NetWorld.FirstPlayerId || body.Role == FreeRole.Waiting)
                return 0f;
            bool certain = false;
            int players = 0;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].NetId >= NetWorld.FirstPlayerId && bodies[i].Role != FreeRole.Waiting)
                {
                    players++;
                    certain |= bodies[i].Odds == Certain;
                }
            int total = 0;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].NetId >= NetWorld.FirstPlayerId && bodies[i].Role != FreeRole.Waiting)
                    total += Weight(bodies[i], true, certain);
            return total == 0 ? 1f / Math.Max(1, players) : (float)Weight(body, true, certain) / total;
        }

        /// <summary>The round's end: the pause as after a win (no winner, Won tells each), and the next draw's
        /// weights (Luca): a catcher who caught nobody is certain; then whoever was neither caught nor free;
        /// else the first caught for half, the other caught ones sharing the other half.</summary>
        void EndFree(IReadOnlyList<SimulationBody> bodies)
        {
            End(bodies, null);
            int standing = 0, caught = 0;
            SimulationBody first = null;
            foreach (SimulationBody b in order)
            {
                b.Odds = 0;
                if (b.Role == FreeRole.Runner)
                    standing++;
                if (b.Role == FreeRole.Caught)
                {
                    caught++;
                    if (first == null || b.CaughtOrder < first.CaughtOrder)
                        first = b;
                }
            }
            foreach (SimulationBody b in order)
            {
                if (b.Role == FreeRole.Catcher && !Won(b, bodies))
                    b.Odds = Certain;
                else if (standing > 0)
                    b.Odds = b.Role == FreeRole.Runner ? (byte)1 : (byte)0;
                else if (b.Role == FreeRole.Caught)
                    b.Odds = b == first ? (byte)Math.Clamp(caught - 1, 1, 254) : (byte)1;
            }
        }

        int Count(FreeRole role)
        {
            int n = 0;
            foreach (SimulationBody b in order)
                n += b.Role == role ? 1 : 0;
            return n;
        }

        // The bodies by NetId into order: the list's own order stays out of every result.
        void Sorted(IReadOnlyList<SimulationBody> bodies)
        {
            order.Clear();
            for (int i = 0; i < bodies.Count; i++)
                order.Add(bodies[i]);
            SortByNetId(order);
        }
    }
}
