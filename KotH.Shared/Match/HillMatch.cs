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

    /// <summary>What a networked game's round is part of: a lone round (Single, never held), the lobby on
    /// Mount Destiny that waits and counts down, or a round on the chosen map. Lobby and Round hold where
    /// the host changes maps; every peer has the same stage (HostSession sends it).</summary>
    public enum MatchStage : byte { Single, Lobby, Round }

    /// <summary>Solo: everyone for themselves. Duo: teams of two, chained together, paired at the round's
    /// start (Luca). Chosen by whoever starts the game; travels with the state.</summary>
    public enum MatchMode : byte { Solo, Duo }

    /// <summary>Everything a round keeps besides the bodies' own scores. Part of a
    /// snapshot: a restore puts the phase, the clocks and who stood in the zone back
    /// exactly as that tick left them.</summary>
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

        /// <summary>Who holds the zone: the one body alone in it while it takes the zone,
        /// then whoever took it for as long as it stays inside, even with others in. Null
        /// for none, or several while nobody has it. In duo the team's lead (HillMatch.LeadOf).</summary>
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

        /// <summary>Solo or teams of two.</summary>
        public MatchMode Mode;

        /// <summary>The round has ended and the winner is shown.</summary>
        public readonly bool Over => Phase == MatchPhase.Over;
    }

    /// <summary>King of the hill: alone in the zone for a few seconds takes it, then a point per
    /// second while the holder stays inside, whoever comes in (Luca's rule). First to the winning
    /// points wins, else the leader at the time limit. Ticked by SimulationSystem after combat;
    /// with MinPlayers a round waits for players and counts down (dummies do not count).</summary>
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

        /// <summary>Lobby: the countdown stops at 0 instead of starting the round (Countdown, PhaseTicks 0).
        /// Round: the win pause stops at 0 instead of a new round (Over, PhaseTicks 0). The host then changes maps.</summary>
        public MatchStage Stage { get; set; }

        /// <summary>Whether the round stands where the host changes maps.</summary>
        public bool Held => state.PhaseTicks == 0 && (Stage == MatchStage.Lobby && state.Phase == MatchPhase.Countdown
                                                      || Stage == MatchStage.Round && state.Phase == MatchPhase.Over);

        public MatchState State => state;

        /// <summary>The game's dice for places round the hill; see MatchState.Seed.</summary>
        public uint Seed
        {
            get => state.Seed;
            set => state.Seed = value;
        }

        /// <summary>Solo or duo; set before the game starts (like MinPlayers, it travels with the state).</summary>
        public MatchMode Mode
        {
            get => state.Mode;
            set => state.Mode = value;
        }

        public bool Duo => state.Mode == MatchMode.Duo;

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
                Mode = state.Mode,
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

            // Into a running round: a partner among those alone, then straight to a place round the hill.
            if (moving.Count > 0 && state.Phase == MatchPhase.Running && Duo)
                PairLone(bodies);
            if (moving.Count > 0 && state.Phase == MatchPhase.Running && HasSpawnRing)
                Place(bodies);
        }

        /// <summary>One tick of the round on the poses this tick published. Only the
        /// count and the single body inside matter, so the order of the list stays out of
        /// the result.</summary>
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
                    if (!moving.Contains(bodies[i]))
                        moving.Add(bodies[i]);
                    // The chained partner comes along, to stand beside it (Luca).
                    if (PartnerOf(bodies[i], bodies) is { HasSpawn: true } mate && !moving.Contains(mate))
                        moving.Add(mate);
                }
            if (moving.Count > 0)
            {
                if (ring)
                    Place(bodies);
                else
                    Respawn(bodies);
            }

            switch (state.Phase)
            {
                case MatchPhase.Over:
                    if (--state.PhaseTicks > 0)
                        return;
                    if (Stage == MatchStage.Round)
                    {
                        state.PhaseTicks = 0;
                        return;
                    }
                    for (int i = 0; i < count; i++)
                        bodies[i].ResetToSpawn();
                    Reset();
                    if (state.Phase == MatchPhase.Running)
                    {
                        PairEveryone(bodies);
                        PlaceEveryone(bodies);
                    }
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
                    if (Stage == MatchStage.Lobby)
                    {
                        state.PhaseTicks = 0;
                        return;
                    }
                    for (int i = 0; i < count; i++)
                        bodies[i].ResetToSpawn();
                    state = new MatchState { MinPlayers = state.MinPlayers, Seed = state.Seed, Mode = state.Mode };
                    PairEveryone(bodies);
                    PlaceEveryone(bodies);
                    return;
            }

            Teams(bodies);
            int inside = 0, teamsIn = 0, fullTeams = 0;
            SimulationBody only = null, full = null;
            bool holderIn = false;
            for (int i = 0; i < count; i++)
            {
                if (!ins[i])
                    continue;
                inside++;
                SimulationBody lead = leads[i];
                holderIn |= lead == state.Holder;
                // Each team inside once: at its lead, or at the partner while the lead is out.
                int mate = mates[i];
                if (lead == bodies[i])
                {
                    teamsIn++;
                    only = lead;
                    if (mate >= 0 && ins[mate])
                    {
                        fullTeams++;
                        full = lead;
                    }
                }
                else if (!ins[mate])
                {
                    teamsIn++;
                    only = lead;
                }
            }

            state.Inside = inside;
            // Taken (warmed up) the zone stays its holder's team's while one of it is inside.
            // Taking it needs the team alone in it, or both partners against at most one of
            // every other team; two full teams contest it (Luca). Alone, a team is one body.
            bool taken = holderIn && state.Holder.AloneTicks > WarmupTicks;
            if (!taken)
                state.Holder = teamsIn == 1 ? only : fullTeams == 1 ? full : null;

            // The warm-up starts over as soon as a team leaves or shares the zone
            // before it is taken; both partners count alike and carry the team's points.
            for (int i = 0; i < count; i++)
            {
                SimulationBody body = bodies[i];
                if (leads[i] != state.Holder)
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

        // This tick's teams: per body whether it stands in the zone, its partner's index (-1 alone) and its
        // team's lead. A partner gone (left) leaves the body alone for good.
        readonly List<bool> ins = new List<bool>();
        readonly List<int> mates = new List<int>();
        readonly List<SimulationBody> leads = new List<SimulationBody>();

        void Teams(IReadOnlyList<SimulationBody> bodies)
        {
            ins.Clear();
            mates.Clear();
            leads.Clear();
            for (int i = 0; i < bodies.Count; i++)
            {
                SimulationBody body = bodies[i];
                ins.Add(Contains(body.Motor.Position));
                int mate = IndexOfPartner(body, bodies);
                if (mate < 0)
                    body.Partner = 0;
                mates.Add(mate);
                leads.Add(mate >= 0 && bodies[mate].NetId < body.NetId ? bodies[mate] : body);
            }
            if (state.Holder != null)
                state.Holder = LeadOf(state.Holder, bodies);
        }

        static int IndexOfPartner(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
        {
            if (body.Partner == 0)
                return -1;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].NetId == body.Partner && bodies[i] != body)
                    return i;
            return -1;
        }

        /// <summary>The body's chained partner among bodies; null alone or with the partner gone.</summary>
        public static SimulationBody PartnerOf(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
        {
            int i = IndexOfPartner(body, bodies);
            return i >= 0 ? bodies[i] : null;
        }

        /// <summary>Who stands for the body's team: of a pair the lower NetId, else the body itself.</summary>
        public static SimulationBody LeadOf(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
            => PartnerOf(body, bodies) is { } mate && mate.NetId < body.NetId ? mate : body;

        /// <summary>Whether the two are one team: the same body or partners.</summary>
        public static bool SameTeam(SimulationBody a, SimulationBody b)
            => a == b || a != null && b != null && a.Partner != 0 && a.Partner == b.NetId && b.Partner == a.NetId;

        // ---------- pairs (duo) ----------

        readonly List<SimulationBody> lone = new List<SimulationBody>();
        readonly List<SimulationBody> loneBots = new List<SimulationBody>();

        /// <summary>A new round's teams: everyone apart, then paired afresh.</summary>
        void PairEveryone(IReadOnlyList<SimulationBody> bodies)
        {
            if (!Duo)
                return;
            for (int i = 0; i < bodies.Count; i++)
                bodies[i].Partner = 0;
            PairLone(bodies);
        }

        /// <summary>Pairs the players without a partner: first who came together (both ways, then one
        /// way), then at random from the game's seed and the tick, players with players and a bot to
        /// fill; one may stay alone. By NetId, so the list's order stays out of it.</summary>
        void PairLone(IReadOnlyList<SimulationBody> bodies)
        {
            lone.Clear();
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].NetId >= NetWorld.FirstPlayerId && IndexOfPartner(bodies[i], bodies) < 0)
                    lone.Add(bodies[i]);
            SortByNetId(lone);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < lone.Count; i++)
                {
                    SimulationBody a = lone[i];
                    if (a.Partner != 0 || a.Wish == 0)
                        continue;
                    for (int j = 0; j < lone.Count; j++)
                        if (lone[j].NetId == a.Wish && lone[j] != a && lone[j].Partner == 0 && (pass == 1 || lone[j].Wish == a.NetId))
                        {
                            Tie(a, lone[j]);
                            break;
                        }
                }

            // The rest shuffled: players first, bots after, so a bot only fills.
            loneBots.Clear();
            for (int i = lone.Count - 1; i >= 0; i--)
                if (lone[i].Partner != 0 || lone[i].Bot)
                {
                    if (lone[i].Partner == 0)
                        loneBots.Insert(0, lone[i]);
                    lone.RemoveAt(i);
                }
            int tick = bodies.Count > 0 ? bodies[0].Simulation?.CurrentTick ?? 0 : 0;
            uint dice = Mix(state.Seed ^ Mix((uint)tick * 2246822519u ^ 0x5BD1E995u));
            Shuffle(lone, ref dice);
            Shuffle(loneBots, ref dice);
            lone.AddRange(loneBots);
            for (int i = 0; i + 1 < lone.Count; i += 2)
                Tie(lone[i], lone[i + 1]);
            lone.Clear();
            loneBots.Clear();
        }

        // Partners share the team's points: the newer one takes the other's.
        static void Tie(SimulationBody a, SimulationBody b)
        {
            a.Partner = b.NetId;
            b.Partner = a.NetId;
            SimulationBody ahead = a.ScoreTicks >= b.ScoreTicks ? a : b;
            a.ScoreTicks = b.ScoreTicks = ahead.ScoreTicks;
            a.AloneTicks = b.AloneTicks = ahead.AloneTicks;
        }

        static void Shuffle(List<SimulationBody> list, ref uint dice)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Math.Min(i, (int)(Unit(ref dice) * (i + 1)));
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        static void SortByNetId(List<SimulationBody> list)
        {
            // Insertion sort, stable: a handful of bodies.
            for (int i = 1; i < list.Count; i++)
                for (int j = i; j > 0 && list[j - 1].NetId > list[j].NetId; j--)
                    (list[j - 1], list[j]) = (list[j], list[j - 1]);
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

        /// <summary>The team (its lead) with the most points, null when the top is shared.</summary>
        SimulationBody Leader(IReadOnlyList<SimulationBody> bodies)
        {
            SimulationBody best = null;
            int top = -1;
            bool shared = false;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (leads[i] != bodies[i])
                    continue;
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

        /// <summary>Puts the bodies in moving at random places in the ring, facing the
        /// zone, each at least the spacing from everyone else. By NetId, so the list's
        /// order stays out of it; the dice come from the game's seed, the tick and the
        /// NetId, the ground from the arena, so every peer agrees.</summary>
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
                // Beside the partner, if it stays or was placed just now (lower NetId first).
                if (PartnerOf(body, bodies) is { } mate && (!moving.Contains(mate) || mate.NetId < body.NetId))
                {
                    PlaceNear(body, mate);
                    taken.Add(body.Motor.Position);
                    continue;
                }
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

        /// <summary>Back at the start places, without a ring: a pair's second beside the first.</summary>
        void Respawn(IReadOnlyList<SimulationBody> bodies)
        {
            SortByNetId(moving);
            foreach (SimulationBody body in moving)
            {
                if (PartnerOf(body, bodies) is { } mate && moving.Contains(mate) && mate.NetId < body.NetId)
                    PlaceNear(body, mate);
                else
                    body.Respawn();
            }
            moving.Clear();
        }

        // Partners start this far apart: well inside the chain's reach.
        const float NearMin = 1.5f, NearMax = 2.5f;

        /// <summary>Puts the body on open ground beside mate, about as high, facing the zone; with none
        /// found right where mate stands (SeparateBodies pushes them apart). Dice as Place's.</summary>
        void PlaceNear(SimulationBody body, SimulationBody mate)
        {
            Vector3 at = mate.Motor.Position;
            float foot = at.Y - mate.Motor.StandHeight * 0.5f;
            int tick = body.Simulation?.CurrentTick ?? 0;
            uint dice = Mix(state.Seed ^ Mix((uint)tick * 2654435761u ^ (uint)body.NetId * 40503u ^ 0x27D4EB2Fu));
            Vector3 best = at;
            for (int k = 0; k < 16; k++)
            {
                float angle = Unit(ref dice) * 2f * MathF.PI, r = UMath.Lerp(NearMin, NearMax, Unit(ref dice));
                if (Ground(body, at.X + r * UMath.Cos(angle), at.Z + r * UMath.Sin(angle), out Vector3 ground)
                    && UMath.Abs(ground.Y - foot) < 1f)
                {
                    best = new Vector3(ground.X, ground.Y + body.Motor.StandHeight * 0.5f + 0.05f, ground.Z);
                    break;
                }
            }
            float yaw = UMath.Atan2(-(center.X - best.X), -(center.Z - best.Z)) * UMath.Rad2Deg;
            body.PlaceAt(best, yaw);
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
