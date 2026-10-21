using System;
using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Shared.Match
{
    /// <summary>
    /// Where a round stands. Running and Over keep the values 0 and 1 they
    /// had as a flag; the lobby phases only come up with MinPlayers set.
    /// </summary>
    public enum MatchPhase : byte { Running, Over, Waiting, Countdown }

    /// <summary>
    /// Everything a round keeps besides the bodies' own scores. Part of a
    /// snapshot: a restore puts the phase, the clocks and who stood in the
    /// zone back exactly as that tick left them.
    /// </summary>
    public struct MatchState
    {
        /// <summary>Ticks the round has run, frozen once it is over.</summary>
        public int RoundTicks;

        public MatchPhase Phase;

        /// <summary>Ticks left of the countdown or of the pause after a win.</summary>
        public int PhaseTicks;

        /// <summary>Who won; null while running, and for a draw.</summary>
        public SimulationBody Winner;

        /// <summary>How many bodies stood in the zone in the last tick.</summary>
        public int Inside;

        /// <summary>
        /// Who holds the zone: the one body alone in it while it takes the
        /// zone, then whoever took it for as long as it stays inside, even
        /// with others in. Null for none, or several while nobody has it.
        /// </summary>
        public SimulationBody Holder;

        /// <summary>
        /// Players needed for the countdown, 0 to start at once (offline).
        /// Kept in the round so a joining peer learns it with the state.
        /// </summary>
        public int MinPlayers;

        /// <summary>The round has ended and the winner is shown.</summary>
        public readonly bool Over => Phase == MatchPhase.Over;
    }

    /// <summary>
    /// King of the hill. Whoever stands in the zone alone warms up for a
    /// few seconds and takes it, then earns a point per second for as long
    /// as it stays inside, whoever comes in too (Luca's rule). Two inside
    /// during the warm-up take nobody's zone. First to the winning points takes the round, else the
    /// leader when time runs out. Ticked by SimulationSystem after combat.
    /// With MinPlayers set a round waits for enough players and counts
    /// down first; only players count, not the dummies.
    /// </summary>
    public sealed class HillMatch
    {
        public const int TicksPerPoint = 40;

        readonly Vector3 center;
        readonly Vector3 half;
        readonly Quaternion inverse;

        MatchState state;

        public HillMatch(CaptureZone zone)
        {
            center = zone.Entity.Transform.Position;
            half = UMath.Mul(zone.Size, 0.5f);

            // A unit quaternion's inverse is its conjugate.
            Quaternion r = zone.Entity.Transform.Rotation;
            inverse = new Quaternion(-r.X, -r.Y, -r.Z, r.W);

            RespawnBelow = zone.RespawnBelow;
            WarmupTicks = Ticks(zone.WarmupSeconds);
            WinTicks = zone.WinPoints * TicksPerPoint;
            LimitTicks = Ticks(zone.TimeLimitMinutes * 60f);
            PauseTicks = Ticks(zone.WinPauseSeconds);
            CountdownTicks = Ticks(zone.CountdownSeconds);
            JoinTicks = Ticks(zone.JoinSeconds);
        }

        public const int DefaultMinPlayers = 4;

        /// <summary>A body that falls below this height is back at its spawn.</summary>
        public float RespawnBelow { get; set; }

        public int WarmupTicks { get; set; }
        public int WinTicks { get; set; }
        public int LimitTicks { get; set; }
        public int PauseTicks { get; set; }
        public int CountdownTicks { get; set; }

        /// <summary>How long into a running round players may still join.</summary>
        public int JoinTicks { get; set; }

        public MatchState State => state;

        /// <summary>The middle of the zone's box and half its size, for the bots.</summary>
        public Vector3 Center => center;
        public Vector3 HalfSize => half;

        /// <summary>Players needed to start; setting it starts the round over.</summary>
        public int MinPlayers
        {
            get => state.MinPlayers;
            set
            {
                state.MinPlayers = Math.Max(0, value);
                Reset();
            }
        }

        /// <summary>
        /// Whether a host takes a new player now: while waiting, counting
        /// down, after a win, and in the first JoinTicks of a round.
        /// </summary>
        public bool AcceptsJoin => state.Phase != MatchPhase.Running || state.RoundTicks < JoinTicks;

        /// <summary>Players among the bodies: everyone from the first player NetId on.</summary>
        public static int CountPlayers(IReadOnlyList<SimulationBody> bodies)
        {
            int players = 0;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].NetId >= NetWorld.FirstPlayerId)
                    players++;
            return players;
        }

        /// <summary>Whole points out of score ticks, as the HUD shows them.</summary>
        public static int Points(int scoreTicks) => scoreTicks / TicksPerPoint;

        public bool Contains(Vector3 point)
        {
            Vector3 local = UMath.Rotate(inverse, UMath.Sub(point, center));
            return UMath.Abs(local.X) <= half.X && UMath.Abs(local.Y) <= half.Y &&
                   UMath.Abs(local.Z) <= half.Z;
        }

        /// <summary>Puts the round back to a captured tick.</summary>
        public void Restore(in MatchState from) => state = from;

        /// <summary>A fresh round, or a fresh wait for players; the bodies are not touched.</summary>
        public void Reset()
            => state = new MatchState
            {
                MinPlayers = state.MinPlayers,
                Phase = state.MinPlayers > 0 ? MatchPhase.Waiting : MatchPhase.Running,
            };

        /// <summary>
        /// Remembers where a body joined, before its first tick, so a new
        /// round can put it back there.
        /// </summary>
        public void MarkNewcomers(IReadOnlyList<SimulationBody> bodies)
        {
            for (int i = 0; i < bodies.Count; i++)
                if (!bodies[i].HasSpawn)
                    bodies[i].MarkSpawn();
        }

        /// <summary>
        /// One tick of the round on the poses this tick published. Only the
        /// count and the single body inside matter, so the order of the list
        /// stays out of the result.
        /// </summary>
        public void Tick(IReadOnlyList<SimulationBody> bodies)
        {
            int count = bodies.Count;

            // Out of the arena: back to where it joined, in every phase.
            for (int i = 0; i < count; i++)
                if (bodies[i].HasSpawn && bodies[i].Motor.Position.Y < RespawnBelow)
                    bodies[i].Respawn();

            switch (state.Phase)
            {
                case MatchPhase.Over:
                    if (--state.PhaseTicks > 0)
                        return;
                    for (int i = 0; i < count; i++)
                        bodies[i].ResetToSpawn();
                    Reset();
                    return;

                case MatchPhase.Waiting:
                    // The zone does not count before the round; the count is checked every tick.
                    if (CountPlayers(bodies) >= state.MinPlayers)
                    {
                        state.Phase = MatchPhase.Countdown;
                        state.PhaseTicks = Math.Max(CountdownTicks, 1);
                    }
                    return;

                case MatchPhase.Countdown:
                    if (CountPlayers(bodies) < state.MinPlayers)
                    {
                        state.Phase = MatchPhase.Waiting;
                        state.PhaseTicks = 0;
                        return;
                    }
                    if (--state.PhaseTicks > 0)
                        return;
                    for (int i = 0; i < count; i++)
                        bodies[i].ResetToSpawn();
                    state = new MatchState { MinPlayers = state.MinPlayers };
                    return;
            }

            int inside = 0;
            SimulationBody one = null;
            bool holderIn = false;
            for (int i = 0; i < count; i++)
            {
                if (!Contains(bodies[i].Motor.Position))
                    continue;
                inside++;
                one = bodies[i];
                holderIn |= bodies[i] == state.Holder;
            }

            state.Inside = inside;
            // Taken (warmed up) the zone stays its holder's while it is inside;
            // taking it needs the zone alone.
            bool taken = holderIn && state.Holder.AloneTicks > WarmupTicks;
            if (!taken)
                state.Holder = inside == 1 ? one : null;

            // The warm-up starts over as soon as a body leaves or shares the zone
            // before it is taken.
            for (int i = 0; i < count; i++)
            {
                SimulationBody body = bodies[i];
                if (body != state.Holder)
                {
                    body.AloneTicks = 0;
                    continue;
                }

                body.AloneTicks++;
                if (body.AloneTicks > WarmupTicks)
                    body.ScoreTicks++;
            }

            state.RoundTicks++;

            if (state.Holder != null && state.Holder.ScoreTicks >= WinTicks)
                End(bodies, state.Holder);
            else if (state.RoundTicks >= LimitTicks)
                End(bodies, Leader(bodies));
        }

        void End(IReadOnlyList<SimulationBody> bodies, SimulationBody winner)
        {
            state.Phase = MatchPhase.Over;
            state.Winner = winner;
            state.PhaseTicks = Math.Max(PauseTicks, 1);
            state.Holder = null;
            for (int i = 0; i < bodies.Count; i++)
                bodies[i].AloneTicks = 0;
        }

        /// <summary>The body with the most points, null when the top is shared.</summary>
        static SimulationBody Leader(IReadOnlyList<SimulationBody> bodies)
        {
            SimulationBody best = null;
            int top = -1;
            bool shared = false;
            for (int i = 0; i < bodies.Count; i++)
            {
                int score = bodies[i].ScoreTicks;
                if (score > top)
                {
                    top = score;
                    best = bodies[i];
                    shared = false;
                }
                else if (score == top)
                    shared = true;
            }

            return shared ? null : best;
        }

        static int Ticks(float seconds) => (int)Math.Round(seconds / SimulationSystem.TickSeconds);
    }
}
