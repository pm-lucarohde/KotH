using System;
using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Shared.Match
{
    /// <summary>
    /// Everything a round keeps besides the bodies' own scores. Part of a
    /// snapshot: a restore puts the clock, the pause and who stood in the
    /// zone back exactly as that tick left them.
    /// </summary>
    public struct MatchState
    {
        /// <summary>Ticks the round has run, frozen once it is over.</summary>
        public int RoundTicks;

        /// <summary>The round has ended and the winner is shown.</summary>
        public bool Over;

        /// <summary>Ticks of the pause left before the next round.</summary>
        public int PauseTicks;

        /// <summary>Who won; null while running, and for a draw.</summary>
        public SimulationBody Winner;

        /// <summary>How many bodies stood in the zone in the last tick.</summary>
        public int Inside;

        /// <summary>The one body alone in the zone, null for none or several.</summary>
        public SimulationBody Holder;
    }

    /// <summary>
    /// King of the hill. Whoever stands in the zone alone warms up for a
    /// few seconds and then earns a point per second; with two inside
    /// nobody does. First to the winning points takes the round, else the
    /// leader when time runs out. Ticked by SimulationSystem after combat.
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

            WarmupTicks = Ticks(zone.WarmupSeconds);
            WinTicks = zone.WinPoints * TicksPerPoint;
            LimitTicks = Ticks(zone.TimeLimitMinutes * 60f);
            PauseTicks = Ticks(zone.WinPauseSeconds);
        }

        public int WarmupTicks { get; set; }
        public int WinTicks { get; set; }
        public int LimitTicks { get; set; }
        public int PauseTicks { get; set; }

        public MatchState State => state;

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

        /// <summary>A fresh round; the bodies are not touched.</summary>
        public void Reset() => state = default;

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

            if (state.Over)
            {
                if (--state.PauseTicks > 0)
                    return;

                for (int i = 0; i < count; i++)
                    bodies[i].ResetToSpawn();
                state = default;
                return;
            }

            int inside = 0;
            SimulationBody one = null;
            for (int i = 0; i < count; i++)
            {
                if (!Contains(bodies[i].Motor.Position))
                    continue;
                inside++;
                one = bodies[i];
            }

            state.Inside = inside;
            state.Holder = inside == 1 ? one : null;

            // The warm-up starts over as soon as a body leaves or shares the zone.
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
            state.Over = true;
            state.Winner = winner;
            state.PauseTicks = Math.Max(PauseTicks, 1);
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
