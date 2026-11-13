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

    /// <summary>Solo: everyone for themselves. Duo, trio, squad: teams of two, three, four, chained in a row
    /// and formed at the round's start (Luca). Chosen by whoever starts the game; travels with the state.</summary>
    public enum MatchMode : byte { Solo, Duo, Trio, Squad }

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
        /// for none, or several while nobody has it. In teams the team's lead (HillMatch.LeadOf).</summary>
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

        /// <summary>Solo or teams of two to four.</summary>
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
        readonly ZoneShape shape;
        readonly Vector2[] corners;
        readonly float hole;
        readonly float magmaRadius, magmaSurface, magmaWave, magmaRespawn;
        readonly int magmaEvery;

        MatchState state;

        public HillMatch(CaptureZone zone)
        {
            center = zone.Entity.Transform.Position;
            half = UMath.Mul(zone.Size, 0.5f);
            shape = zone.Shape == ZoneShape.Polygon && (zone.Corners?.Count ?? 0) < 3 ? ZoneShape.Box : zone.Shape;
            corners = shape == ZoneShape.Polygon ? zone.Corners.ToArray() : Array.Empty<Vector2>();
            hole = shape == ZoneShape.Cylinder ? UMath.Max(0f, zone.Hole) : 0f;
            if (zone.Entity.Get<MagmaPool>() is { } magma)
                (magmaRadius, magmaSurface, magmaWave, magmaEvery, magmaRespawn) =
                    (magma.Radius, magma.Surface, magma.DamagePerWave, Math.Max(1, Ticks(magma.WaveSeconds)), magma.RespawnAbove);

            // A polygon's half size is the reach of its corners, for the bots' sense of the zone.
            foreach (Vector2 c in corners)
                half = new Vector3(UMath.Max(half.X, UMath.Abs(c.X)), half.Y, UMath.Max(half.Z, UMath.Abs(c.Y)));

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

        /// <summary>Solo or teams; set before the game starts (like MinPlayers, it travels with the state).</summary>
        public MatchMode Mode
        {
            get => state.Mode;
            set => state.Mode = value;
        }

        /// <summary>Bodies per full team: 1 in solo, up to 4.</summary>
        public int TeamSize => Size(state.Mode);

        public static int Size(MatchMode mode) => mode switch { MatchMode.Duo => 2, MatchMode.Trio => 3, MatchMode.Squad => 4, _ => 1 };

        /// <summary>The mode's word on the command line, in settings and lobby data ("duo").</summary>
        public static string ModeName(MatchMode mode) => mode switch
        {
            MatchMode.Duo => "duo", MatchMode.Trio => "trio", MatchMode.Squad => "squad", _ => "solo",
        };

        /// <summary>A mode from its word (any case), null for none.</summary>
        public static MatchMode? ParseMode(string word) => word?.ToLowerInvariant() switch
        {
            "solo" => MatchMode.Solo, "duo" => MatchMode.Duo, "trio" => MatchMode.Trio, "squad" => MatchMode.Squad, _ => null,
        };

        /// <summary>Whether the round plays in chained teams.</summary>
        public bool Teamed => state.Mode != MatchMode.Solo;

        /// <summary>The middle of the zone and half its bounding box, for the bots.</summary>
        public Vector3 Center => center;
        public Vector3 HalfSize => half;

        /// <summary>A ring's inner radius, 0 for a zone without a hole.</summary>
        public float Hole => hole;

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
            if (shape == ZoneShape.Box)
                return UMath.Abs(local.X) <= half.X && UMath.Abs(local.Y) <= half.Y &&
                       UMath.Abs(local.Z) <= half.Z;
            if (UMath.Abs(local.Y) > half.Y)
                return false;
            if (shape == ZoneShape.Cylinder)
            {
                float x = local.X / half.X, z = local.Z / half.Z;
                return x * x + z * z <= 1f && local.X * local.X + local.Z * local.Z >= hole * hole;
            }
            return Inside(corners, local.X, local.Z);
        }

        /// <summary>Magma, in waves (Luca): on every wave tick of the round's clock a body with its feet
        /// under the surface takes a hit's percent, camera shake and recoil; true when it already had
        /// more than the limit (then it respawns).</summary>
        bool Burns(SimulationBody body)
        {
            int tick = body.Simulation?.CurrentTick ?? 0;
            if (magmaRadius <= 0f || body.Damage == null || tick % magmaEvery != 0)
                return false;
            Vector3 p = body.Motor.Position;
            float dx = p.X - center.X, dz = p.Z - center.Z;
            if (p.Y - body.Motor.StandHeight * 0.5f >= magmaSurface || dx * dx + dz * dz > magmaRadius * magmaRadius)
                return false;
            bool over = body.Damage.Percent > magmaRespawn;
            body.Damage.LastHitTick = tick;
            body.Damage.LastHitAway = default;
            body.Damage.LastHitAt = default;
            body.Damage.Add(magmaWave);
            return over;
        }

        /// <summary>Even-odd test of a floor point against the drawn corners (Y holds Z).</summary>
        static bool Inside(Vector2[] c, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = c.Length - 1; i < c.Length; j = i++)
                if ((c[i].Y > z) != (c[j].Y > z) &&
                    x < (c[j].X - c[i].X) * (z - c[i].Y) / (c[j].Y - c[i].Y) + c[i].X)
                    inside = !inside;
            return inside;
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

            // Into a running round: a team with room, then straight to a place round the hill.
            if (moving.Count > 0 && state.Phase == MatchPhase.Running && Teamed)
                FormTeams(bodies);
            if (moving.Count > 0 && state.Phase == MatchPhase.Running && HasSpawnRing)
                Place(bodies);
            else if (moving.Count > 0 && state.Phase == MatchPhase.Running && Teamed)
                LineUp(bodies);
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
                if (bodies[i].HasSpawn && (bodies[i].Motor.Position.Y < RespawnBelow || Burns(bodies[i])))
                {
                    if (!moving.Contains(bodies[i]))
                        moving.Add(bodies[i]);
                    // The chained team comes along, to stand beside it (Luca).
                    if (bodies[i].Team != 0)
                        for (int j = 0; j < count; j++)
                            if (bodies[j].Team == bodies[i].Team && bodies[j].HasSpawn && !moving.Contains(bodies[j]))
                                moving.Add(bodies[j]);
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
                        TeamEveryone(bodies);
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
                    TeamEveryone(bodies);
                    PlaceEveryone(bodies);
                    return;
            }

            ReadTeams(bodies);
            int inside = 0;
            bool holderIn = false;
            for (int i = 0; i < count; i++)
                if (ins[i])
                {
                    inside++;
                    holderIn |= leads[i] == state.Holder;
                    insideOf[IndexOfLead(i, bodies)]++;
                }

            // The team with the most inside, none for a tie at the top.
            SimulationBody most = null;
            int top = 0;
            for (int i = 0; i < count; i++)
                if (insideOf[i] > top)
                {
                    top = insideOf[i];
                    most = bodies[i];
                }
                else if (insideOf[i] == top && top > 0)
                    most = null;

            state.Inside = inside;
            // Taken (warmed up) the zone stays its holder's team's while one of it is inside.
            // Taking it needs more of the team inside than of any other; a tie at the top
            // contests it (Luca). Alone, a team is one body: solo stays as it was.
            bool taken = holderIn && state.Holder.AloneTicks > WarmupTicks;
            if (!taken)
                state.Holder = most;

            // The warm-up starts over as soon as a team leaves or shares the zone
            // before it is taken; the team's members count alike and carry its points.
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

        // This tick's teams: per body whether it stands in the zone and its team's lead (the lowest NetId
        // there), and per lead how many of its team are inside. A team left with one body is none.
        readonly List<bool> ins = new List<bool>();
        readonly List<SimulationBody> leads = new List<SimulationBody>();
        readonly List<int> insideOf = new List<int>();

        void ReadTeams(IReadOnlyList<SimulationBody> bodies)
        {
            ins.Clear();
            leads.Clear();
            insideOf.Clear();
            for (int i = 0; i < bodies.Count; i++)
            {
                SimulationBody body = bodies[i];
                ins.Add(Contains(body.Motor.Position));
                if (body.Team != 0 && Members(body.Team, bodies) < 2)
                    body.Team = 0;
                leads.Add(LeadOf(body, bodies));
                insideOf.Add(0);
            }
            if (state.Holder != null)
                state.Holder = LeadOf(state.Holder, bodies);
        }

        int IndexOfLead(int i, IReadOnlyList<SimulationBody> bodies)
        {
            for (int j = 0; j < bodies.Count; j++)
                if (bodies[j] == leads[i])
                    return j;
            return i;
        }

        static int Members(int team, IReadOnlyList<SimulationBody> bodies)
        {
            int n = 0;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Team == team)
                    n++;
            return n;
        }

        // A body's team id, its own NetId alone.
        static int Key(SimulationBody body) => body.Team != 0 ? body.Team : body.NetId;

        static int Size(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
            => body.Team != 0 ? Members(body.Team, bodies) : 1;

        /// <summary>Who stands for the body's team: its lowest NetId, else the body itself.</summary>
        public static SimulationBody LeadOf(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
        {
            SimulationBody lead = body;
            if (body.Team != 0)
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i].Team == body.Team && bodies[i].NetId < lead.NetId)
                        lead = bodies[i];
            return lead;
        }

        /// <summary>The body's link up the chain: the team's next lower NetId; null for the lead or alone.</summary>
        public static SimulationBody AheadOf(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
        {
            SimulationBody ahead = null;
            if (body.Team != 0)
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i].Team == body.Team && bodies[i].NetId < body.NetId && (ahead == null || bodies[i].NetId > ahead.NetId))
                        ahead = bodies[i];
            return ahead;
        }

        /// <summary>The body's link down the chain: the team's next higher NetId; null for the last or alone.</summary>
        public static SimulationBody BehindOf(SimulationBody body, IReadOnlyList<SimulationBody> bodies)
        {
            SimulationBody behind = null;
            if (body.Team != 0)
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i].Team == body.Team && bodies[i].NetId > body.NetId && (behind == null || bodies[i].NetId < behind.NetId))
                        behind = bodies[i];
            return behind;
        }

        /// <summary>Whether the two are one team: the same body or the same team id.</summary>
        public static bool SameTeam(SimulationBody a, SimulationBody b)
            => a == b || a != null && b != null && a.Team != 0 && a.Team == b.Team;

        // ---------- teams ----------

        readonly List<SimulationBody> lone = new List<SimulationBody>();
        readonly List<SimulationBody> loneBots = new List<SimulationBody>();

        /// <summary>A new round's teams: everyone apart, then teamed afresh.</summary>
        void TeamEveryone(IReadOnlyList<SimulationBody> bodies)
        {
            if (!Teamed)
                return;
            for (int i = 0; i < bodies.Count; i++)
                bodies[i].Team = 0;
            FormTeams(bodies);
        }

        /// <summary>Teams the players with room: first who came together (both ways, then one way), then
        /// teams short of members filled, then the rest at random from the game's seed and the tick in
        /// rows of TeamSize, players first and bots to fill; one may stay alone. By NetId, so the list's
        /// order stays out of it.</summary>
        void FormTeams(IReadOnlyList<SimulationBody> bodies)
        {
            int full = TeamSize;
            lone.Clear();
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].NetId >= NetWorld.FirstPlayerId && Size(bodies[i], bodies) < full)
                    lone.Add(bodies[i]);
            SortByNetId(lone);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < lone.Count; i++)
                {
                    SimulationBody a = lone[i];
                    if (a.Wish == 0)
                        continue;
                    for (int j = 0; j < lone.Count; j++)
                    {
                        SimulationBody b = lone[j];
                        if (b.NetId == a.Wish && !SameTeam(a, b) && (pass == 1 || b.Wish == a.NetId)
                            && Size(a, bodies) + Size(b, bodies) <= full)
                        {
                            Join(a, b, bodies);
                            break;
                        }
                    }
                }

            // Teams short of members (from wishes, or a round's joiners) first, by lead.
            loneBots.Clear();
            for (int i = lone.Count - 1; i >= 0; i--)
                if (lone[i].Team != 0 || lone[i].Bot)
                {
                    if (lone[i].Team == 0)
                        loneBots.Insert(0, lone[i]);
                    lone.RemoveAt(i);
                }
            int tick = bodies.Count > 0 ? bodies[0].Simulation?.CurrentTick ?? 0 : 0;
            uint dice = Mix(state.Seed ^ Mix((uint)tick * 2246822519u ^ 0x5BD1E995u));
            Shuffle(lone, ref dice);
            Shuffle(loneBots, ref dice);
            lone.AddRange(loneBots);
            int next = 0;
            for (int after = 0; next < lone.Count;)
            {
                SimulationBody team = null;
                for (int i = 0; i < bodies.Count; i++)
                {
                    SimulationBody b = bodies[i];
                    if (b.Team != 0 && b.NetId > after && (team == null || b.NetId < team.NetId) && LeadOf(b, bodies) == b)
                        team = b;
                }
                if (team == null)
                    break;
                after = team.NetId;
                while (next < lone.Count && Size(team, bodies) < full)
                    Join(team, lone[next++], bodies);
            }

            // The rest in rows of a full team; a last one on its own stays alone.
            for (; next + 1 < lone.Count; next++)
                for (int k = 1; k < full && next + 1 < lone.Count; k++)
                    Join(lone[next], lone[++next], bodies);
            lone.Clear();
            loneBots.Clear();
        }

        // Two teams become one under the lower id; all share the best points (the newer take the other's).
        static void Join(SimulationBody a, SimulationBody b, IReadOnlyList<SimulationBody> bodies)
        {
            int ka = Key(a), kb = Key(b), id = Math.Min(ka, kb);
            SimulationBody ahead = a.ScoreTicks >= b.ScoreTicks ? a : b;
            int score = ahead.ScoreTicks, alone = ahead.AloneTicks;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] == a || bodies[i] == b || bodies[i].Team != 0 && (bodies[i].Team == ka || bodies[i].Team == kb))
                {
                    bodies[i].Team = id;
                    bodies[i].ScoreTicks = score;
                    bodies[i].AloneTicks = alone;
                }
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
            if (!HasSpawnRing && !Teamed)
                return;
            moving.Clear();
            for (int i = 0; i < bodies.Count; i++)
                moving.Add(bodies[i]);
            if (HasSpawnRing)
                Place(bodies);
            else
                LineUp(bodies);
        }

        /// <summary>Without a ring each starts on its marker; one out of the chain's reach of the next up
        /// the chain (or down, if that one stays) goes beside it, as Place does: from markers far apart
        /// the chain dragged them across the map, on Pyramid into the hall under the pyramid.</summary>
        void LineUp(IReadOnlyList<SimulationBody> bodies)
        {
            SortByNetId(moving);
            foreach (SimulationBody body in moving)
                if ((AheadOf(body, bodies) ?? BehindOf(body, bodies)) is { } mate && (!moving.Contains(mate) || mate.NetId < body.NetId)
                    && Apart(body, mate) > Reach(body))
                    PlaceNear(body, mate);
            moving.Clear();
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
                // Beside the next up the chain (placed just now, lower NetId first), or the next down if it stays.
                if ((AheadOf(body, bodies) ?? BehindOf(body, bodies)) is { } mate && (!moving.Contains(mate) || mate.NetId < body.NetId))
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

        /// <summary>Back at the start places, without a ring: a team's others each beside the one before.</summary>
        void Respawn(IReadOnlyList<SimulationBody> bodies)
        {
            SortByNetId(moving);
            foreach (SimulationBody body in moving)
            {
                if (AheadOf(body, bodies) is { } mate && moving.Contains(mate))
                    PlaceNear(body, mate);
                else
                    body.Respawn();
            }
            moving.Clear();
        }

        static float Reach(SimulationBody body) => body.Simulation?.ChainLength ?? 0f;

        static float Apart(SimulationBody a, SimulationBody b)
        {
            Vector3 p = a.Motor.Position, q = b.Motor.Position;
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            return UMath.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Neighbours in a team start this far apart: well inside the chain's reach.
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
