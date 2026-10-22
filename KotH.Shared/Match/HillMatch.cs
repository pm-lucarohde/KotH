using System;
using System.Collections.Generic;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Physics;
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

        /// <summary>
        /// The dice of this game for the places round the hill, picked by
        /// whoever starts it; travels with the state, so every peer rolls alike.
        /// </summary>
        public uint Seed;

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
            ringInner = UMath.Max(0f, zone.SpawnRingInner);
            ringOuter = UMath.Max(ringInner, zone.SpawnRingOuter);
            spacing = UMath.Max(0f, zone.SpawnSpacing);
        }

        readonly float ringInner, ringOuter, spacing;

        // Share of places picked near someone already standing in the ring, and how far round from them at most.
        const float NearShare = 0.5f;
        const float NearReach = 30f;
        readonly List<SimulationBody> moving = new List<SimulationBody>();
        readonly List<Vector3> taken = new List<Vector3>();

        /// <summary>Whether rounds put everyone at random places round the zone instead of the start places.</summary>
        public bool HasSpawnRing => ringOuter > 0f;

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

        /// <summary>The game's dice for places round the hill; see MatchState.Seed.</summary>
        public uint Seed
        {
            get => state.Seed;
            set => state.Seed = value;
        }

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
                Seed = state.Seed,
                Phase = state.MinPlayers > 0 ? MatchPhase.Waiting : MatchPhase.Running,
            };

        /// <summary>
        /// Remembers where a body joined, before its first tick, so a new
        /// round can put it back there.
        /// </summary>
        public void MarkNewcomers(IReadOnlyList<SimulationBody> bodies)
        {
            moving.Clear();
            for (int i = 0; i < bodies.Count; i++)
                if (!bodies[i].HasSpawn)
                {
                    bodies[i].MarkSpawn();
                    moving.Add(bodies[i]);
                }

            // Into a running round: straight to a place round the hill.
            if (moving.Count > 0 && state.Phase == MatchPhase.Running && HasSpawnRing)
                Place(bodies);
        }

        /// <summary>
        /// One tick of the round on the poses this tick published. Only the
        /// count and the single body inside matter, so the order of the list
        /// stays out of the result.
        /// </summary>
        public void Tick(IReadOnlyList<SimulationBody> bodies)
        {
            int count = bodies.Count;

            // Out of the arena: back to where it joined, in every phase; in a
            // round with a ring to a new place in it (Luca's wish).
            bool ring = HasSpawnRing && (state.Phase == MatchPhase.Running || state.Phase == MatchPhase.Over);
            moving.Clear();
            for (int i = 0; i < count; i++)
                if (bodies[i].HasSpawn && bodies[i].Motor.Position.Y < RespawnBelow)
                {
                    if (ring)
                        moving.Add(bodies[i]);
                    else
                        bodies[i].Respawn();
                }
            if (moving.Count > 0)
                Place(bodies);

            switch (state.Phase)
            {
                case MatchPhase.Over:
                    if (--state.PhaseTicks > 0)
                        return;
                    for (int i = 0; i < count; i++)
                        bodies[i].ResetToSpawn();
                    Reset();
                    if (state.Phase == MatchPhase.Running)
                        PlaceEveryone(bodies);
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
                    state = new MatchState { MinPlayers = state.MinPlayers, Seed = state.Seed };
                    PlaceEveryone(bodies);
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

        // ---------- places round the hill ----------

        void PlaceEveryone(IReadOnlyList<SimulationBody> bodies)
        {
            if (!HasSpawnRing)
                return;
            moving.Clear();
            for (int i = 0; i < bodies.Count; i++)
                moving.Add(bodies[i]);
            Place(bodies);
        }

        /// <summary>
        /// Puts the bodies in moving at random places in the ring, facing the
        /// zone, each at least the spacing from everyone else. By NetId, so
        /// the list's order stays out of it; the dice come from the game's
        /// seed, the tick and the NetId, the ground from the arena, so every
        /// peer agrees.
        /// </summary>
        void Place(IReadOnlyList<SimulationBody> bodies)
        {
            // Insertion sort, stable: a handful of bodies.
            for (int i = 1; i < moving.Count; i++)
                for (int j = i; j > 0 && moving[j - 1].NetId > moving[j].NetId; j--)
                    (moving[j - 1], moving[j]) = (moving[j], moving[j - 1]);

            taken.Clear();
            for (int i = 0; i < bodies.Count; i++)
                if (!moving.Contains(bodies[i]))
                    taken.Add(bodies[i].Motor.Position);

            foreach (SimulationBody body in moving)
            {
                int tick = body.Simulation?.CurrentTick ?? 0;
                uint dice = Mix(state.Seed ^ Mix((uint)tick * 2654435761u ^ (uint)body.NetId * 40503u ^ 0x9E3779B9u));
                float bestRoom = -1f;
                Vector3 best = Vector3.Zero;
                for (int k = 0; k < 48 && bestRoom < spacing; k++)
                {
                    float angle = Unit(ref dice) * 2f * MathF.PI;
                    float r = UMath.Sqrt(UMath.Lerp(ringInner * ringInner, ringOuter * ringOuter, Unit(ref dice)));
                    // Every other time near someone already there (Luca: now close, now far).
                    if (taken.Count > 0 && Unit(ref dice) < NearShare)
                    {
                        Vector3 anchor = taken[Math.Min(taken.Count - 1, (int)(Unit(ref dice) * taken.Count))];
                        float along = UMath.Lerp(spacing, NearReach, Unit(ref dice)) / UMath.Max(r, 1f);
                        angle = UMath.Atan2(anchor.Z - center.Z, anchor.X - center.X) + (Unit(ref dice) < 0.5f ? -along : along);
                    }
                    if (!Ground(body, center.X + r * UMath.Cos(angle), center.Z + r * UMath.Sin(angle), out Vector3 at))
                        continue;
                    float room = float.MaxValue;
                    foreach (Vector3 other in taken)
                    {
                        float dx = other.X - at.X, dz = other.Z - at.Z;
                        room = UMath.Min(room, UMath.Sqrt(dx * dx + dz * dz));
                    }
                    if (room > bestRoom)
                    {
                        bestRoom = room;
                        best = at;
                    }
                }
                if (bestRoom < 0f)
                {
                    body.Respawn();     // no open ground found: its start place
                    continue;
                }
                float yaw = UMath.Atan2(-(center.X - best.X), -(center.Z - best.Z)) * UMath.Rad2Deg;
                var position = new Vector3(best.X, best.Y + body.Motor.StandHeight * 0.5f + 0.05f, best.Z);
                body.PlaceAt(position, yaw);
                taken.Add(position);
            }
            moving.Clear();
        }

        // Open, flat ground under a point: the ray from above and four more
        // a step around it end at nearly the same height (not on a tree).
        bool Ground(SimulationBody body, float x, float z, out Vector3 at)
        {
            at = Vector3.Zero;
            float top = center.Y + 60f;
            var world = body.Motor.World;
            if (world == null || !world.Raycast(new Vector3(x, top, z), UMath.Down, top + 60f, WorldQueries.WorldMask, out QueryHit hit) ||
                hit.Normal.Y < 0.85f)
                return false;
            for (int k = 0; k < 4; k++)
            {
                float px = x + (k == 0 ? 1.2f : k == 1 ? -1.2f : 0f), pz = z + (k == 2 ? 1.2f : k == 3 ? -1.2f : 0f);
                if (!world.Raycast(new Vector3(px, top, pz), UMath.Down, top + 60f, WorldQueries.WorldMask, out QueryHit near) ||
                    UMath.Abs(near.Point.Y - hit.Point.Y) > 0.5f)
                    return false;
            }
            at = hit.Point;
            return true;
        }

        static float Unit(ref uint dice)
        {
            dice = Mix(dice + 0x6D2B79F5u);
            return (dice >> 8) * (1f / 16777216f);
        }

        static uint Mix(uint h)
        {
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }

        static int Ticks(float seconds) => (int)Math.Round(seconds / SimulationSystem.TickSeconds);
    }
}
