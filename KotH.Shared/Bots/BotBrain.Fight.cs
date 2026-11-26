using System;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Shared.Bots
{
    /// <summary>A bot's own way to fight (Luca, 25.11.2026: spread, medium on average), drawn once from its seed:
    /// every factor is 1 on average, so the bots as a whole stay as strong as before.</summary>
    public readonly struct Personality
    {
        public readonly float Aggression;   // how much attacking is worth to it
        public readonly float Caution;      // how much its own damage holds it back
        public readonly float Accuracy;     // times the aim error (less is better)
        public readonly float Hand;         // times the hand's acceleration
        public readonly float Blaster;      // times the blaster's range
        public readonly int Focus;          // ticks a target holds its attention before another may take it

        Personality(float aggression, float caution, float accuracy, float hand, float blaster, int focus)
        {
            Aggression = aggression;
            Caution = caution;
            Accuracy = accuracy;
            Hand = hand;
            Blaster = blaster;
            Focus = focus;
        }

        /// <summary>From the bot's seed with dice of its own, so the decisions' xorshift runs as before.</summary>
        public static Personality From(uint seed)
        {
            uint r = seed * 0x2545F491u ^ 0x68E31DA4u | 1u;
            for (int i = 0; i < 4; i++)
                BotRandom.Next(ref r);
            return new Personality(1f + BotRandom.Signed(ref r, 0.3f), 1f + BotRandom.Signed(ref r, 0.4f),
                                   1f + BotRandom.Signed(ref r, 0.4f), 1f + BotRandom.Signed(ref r, 0.2f),
                                   1f + BotRandom.Signed(ref r, 0.3f), 60 + (int)(BotRandom.Unit(ref r) * 80f));
        }
    }

    public sealed partial class BotBrain
    {
        readonly Personality persona;
        int targetSince;             // tick the current target was taken
        int thinkTick;
        int duelSide = 1, duelUntil = -1;   // the way round a target, and till when
        int watchId;                 // holding: the opponent coming up that the view waits for
        Vector2 heldKeys;
        int keysSince;

        public Personality Persona => persona;
        public int FightStage => (int)fightPhase;   // probes: 0 approach, 1 duel, 2 backing off

        public Vector3 SwingEye, SwingCentre;   // probes: where the last swing thought itself and its target
        public int SwingFrom;

        // Hold over attack: alone in the zone it is to be taken (3 s), held it is to be kept.
        const float HoldAlone = 1.6f, HoldKept = 1.25f, HoldShared = 0.4f;

        // Two already at a target are enough (in the zone a little less so); one already at it is a fight to join.
        // The points leader draws a little more (much more sent the bots after it all over Atrium).
        const float Crowded = 0.5f, HolderCrowded = 0.7f, ThirdParty = 0.25f, LeaderBonus = 0.35f, Busy = 3f;

        // A target newly preferred while the old one has not held attention for Focus ticks (1.5 to 3.5 s) counts
        // this much less, and less again while circling the old one: a fight is fought out (80 switches a minute before).
        const float Distracted = 0.5f, Dueling = 0.75f;

        /// <summary>The worth of attacking one opponent, on top of the base score (ThinkHill): this bot's nature,
        /// its damage, how many already fight there, and who leads.</summary>
        float FightWorth(WorldSnapshot view, int i, float score, Vector3 there, bool isHolder, int ownDamage, int bestScore)
        {
            BodyState s = view.StateAt(i);
            int near = 0;
            for (int k = 0; k < view.Count; k++)
            {
                if (k == i || view.BodyAt(k).NetId == netId || view.BodyAt(k).NetId == 0)
                    continue;
                BodyState o = view.StateAt(k);
                if (team != 0 && o.Team == team || s.Team != 0 && o.Team == s.Team)
                    continue;
                near += UMath.Magnitude(UMath.Sub(o.Motor.Position, there)) < Busy ? 1 : 0;
            }
            if (near == 1)
                score += ThirdParty;
            else if (near >= 2)
                score *= isHolder ? HolderCrowded : Crowded;
            if (bestScore > 0)
                score += LeaderBonus * s.ScoreTicks / (float)bestScore;
            score *= persona.Aggression * (1f - 0.4f * persona.Caution * UMath.Min(1f, ownDamage / 150f));
            int id = view.BodyAt(i).NetId;
            if (intent == BotIntent.Attack && target != 0 && target != id)
                score *= (thinkTick - targetSince < persona.Focus ? Distracted : 1f) * (fightPhase == FightPhase.Duel ? Dueling : 1f);
            return score;
        }

        // The nearest opponent outside the zone within this far, whom a holder watches come.
        const float WatchRange = 30f;

        int Watch(WorldSnapshot view, Vector3 at)
        {
            int best = 0;
            float bestD = WatchRange;
            for (int i = 0; i < view.Count; i++)
            {
                SimulationBody o = view.BodyAt(i);
                BodyState s = view.StateAt(i);
                if (o.NetId == netId || o.NetId == 0 || team != 0 && s.Team == team || match.Contains(s.Motor.Position))
                    continue;
                float d = UMath.Magnitude(UMath.Sub(s.Motor.Position, at));
                if (d < bestD)
                {
                    bestD = d;
                    best = o.NetId;
                }
            }
            return best;
        }

        /// <summary>Strafes go round a target one way for 0.5 to 1.5 s, then the other: no side per slot.</summary>
        float DuelSide(int u)
        {
            if (u >= duelUntil)
            {
                duelSide = duelUntil < 0 || BotRandom.Unit(ref rng) < 0.7f ? -duelSide : duelSide;
                duelUntil = u + 20 + (int)(BotRandom.Unit(ref rng) * 40f);
            }
            return duelSide;
        }

        // The swing is a ray from the eye: the view must lie on the body as seen (half a body wide, a little
        // less for the drift of the next ticks), not just near it.
        const float HullHalf = 0.32f, HullHalfHeight = 0.7f;

        bool OnTarget(float bearYaw, float bearPitch, float distance)
        {
            float d = UMath.Max(distance, 0.5f);
            return UMath.Abs(MathF.IEEERemainder(bearYaw - yaw, 360f)) < UMath.Atan2(HullHalf, d) * (180f / MathF.PI)
                   && UMath.Abs(bearPitch - pitch) < UMath.Atan2(HullHalfHeight, d) * (180f / MathF.PI);
        }

        /// <summary>The own eye now, from the own keys since the seen state (Predict): a player knows where they
        /// stand, only the others are seen late. Aiming from the old eye missed most swings by 0.9 m.</summary>
        Vector3 EyeNow(int u, in BodyState me)
        {
            JumpPilot.Body b = Predict(u, me.Motor);
            Vector3 up = UMath.Sub(me.Motor.EyePosition, me.Motor.Position);
            return new Vector3(b.X + up.X, b.Y + body.StandHeight * 0.5f + up.Y, b.Z + up.Z);
        }

        /// <summary>A fight in phases (Luca's plan): run in, circle and swing up close, back off to the zone's middle
        /// after a heavy knock or when far more hurt than the target near an edge, then in again.</summary>
        enum FightPhase : byte { Approach, Duel, Disengage }

        FightPhase fightPhase;
        int backOffUntil = -1, backOffAgain = -1, knockedAt = -1000;

        // Maneuver weights per phase, in Maneuver order (as Weights): sprint in and slide; strafe round and swing;
        // walk back, the view kept on the target.
        static readonly float[][] FightWeights =
        {
            new[] { 0.5f, 4f, 0.3f, 0.3f, 0f, 0.6f, 1.5f, 2.5f, 0f, 1f },        // Approach
            new[] { 1.5f, 0.4f, 2.5f, 2.5f, 0.5f, 0.6f, 0.6f, 0.2f, 0.1f, 2f },   // Duel
            new[] { 2f, 1.5f, 0.6f, 0.6f, 0f, 0.2f, 0.2f, 0f, 0f, 0.3f },         // Disengage
        };

        // Close enough to circle; a knock this fast (m/s) is a heavy hit, backed off from within this many ticks.
        // More hurt than this (by the bot's caution) and this much more than the target near an edge backs off too.
        const float DuelRange = 4f, HeavyKnock = 5f, HighDamage = 90f, DamageLead = 30f;
        const int HurtTicks = 40, BackOffTicks = 30, BackOffSpread = 40, BackOffRest = 160;

        float[] PlanWeights => intent == BotIntent.Attack ? FightWeights[(int)fightPhase] : Weights[(int)intent];

        /// <summary>The phase at a think; backing off sets the goal to the zone's middle.</summary>
        FightPhase PhaseOf(int u, WorldSnapshot view, int self, Vector3 at)
        {
            int ti = intent == BotIntent.Attack ? IndexOf(view, target) : -1;
            if (ti < 0)
                return FightPhase.Approach;
            if (u < backOffUntil)
            {
                goal = Middle(at);
                return FightPhase.Disengage;
            }
            BodyState t = view.StateAt(ti), me = view.StateAt(self);
            float d = Flat(at, Ahead(t.Motor.Position, t.Motor.Velocity, Reaction * Dt));
            bool hill = match != null && view.Match.Mode != MatchMode.Free && NearHill(at);
            // 1, 2, 3, Free: a runner shooting at the catcher in the zone keeps moving side to side.
            if (match != null && view.Match.Mode == MatchMode.Free && (FreeRole)me.Role is FreeRole.Runner or FreeRole.Rescuing
                && match.Contains(t.Motor.Position) && d >= DuelRange)
                return FightPhase.Duel;
            bool hurt = u - knockedAt < HurtTicks;
            bool outmatched = me.DamagePercent * persona.Caution > HighDamage && me.DamagePercent - t.DamagePercent > DamageLead
                              && nav.NearestEdge(Feet(at), 3f, out _, out _);
            if (hill && d < 6f && u >= backOffAgain && (hurt || outmatched))
            {
                backOffUntil = u + BackOffTicks + (int)(BotRandom.Unit(ref rng) * BackOffSpread);
                backOffAgain = backOffUntil + BackOffRest;
                goal = Middle(at);
                return FightPhase.Disengage;
            }
            return d < DuelRange ? FightPhase.Duel : FightPhase.Approach;
        }

        // A few metres towards the zone's middle (on a ring, its middle circle), on the grid.
        Vector3 Middle(Vector3 at)
        {
            Vector3 c = match.Center, to = new Vector3(c.X - at.X, 0f, c.Z - at.Z);
            float reach = UMath.Min(match.HalfSize.X, match.HalfSize.Z), d = UMath.Magnitude(to);
            if (match.Hole > 0f && d > 1e-3f)
                to = UMath.Mul(to, (d - (match.Hole + reach) * 0.5f) / d);
            float step = UMath.Magnitude(to);
            Vector3 p = step > 4f ? UMath.Add(Feet(at), UMath.Mul(to, 4f / step)) : UMath.Add(Feet(at), to);
            return nav.Nearest(p, out Vector3 onMesh) ? onMesh : Feet(at);
        }

        // Holding: a new spot a few metres off every few seconds, as a player shifts on the hill; only where
        // PickSpot would stand too (the zone's inner part) and 2 m from any edge, or it walks off in the end.
        const int SpotTicks = 120, SpotSpread = 160;
        const float SpotShift = 3f, SpotEdge = 2f;
        int spotUntil = -1;

        void ShiftSpot(int u)
        {
            if (hasSpot && u < spotUntil)
                return;
            spotUntil = u + SpotTicks + (int)(BotRandom.Unit(ref rng) * SpotSpread);
            if (!hasSpot)
            {
                PickSpot();
                return;
            }
            Vector3 old = spot;
            for (int k = 0; k < 4; k++)
            {
                PickSpot();
                if (Flat(spot, old) < SpotShift * 1.5f && !nav.NearestEdge(spot, SpotEdge, out _, out _))
                    return;
            }
            spot = old;
        }

        // Holding with nobody near: the view goes round the zone's edges, outwards, a new way every 1.5 to 3 s.
        float scanYaw;
        int scanUntil = -1;

        float Scan(int u, Vector3 at)
        {
            if (u >= scanUntil)
            {
                float outward = UMath.Atan2(-(at.X - match.Center.X), -(at.Z - match.Center.Z)) * (180f / MathF.PI);
                scanYaw = outward + BotRandom.Signed(ref missRng, 80f);
                scanUntil = u + 60 + (int)(BotRandom.Unit(ref missRng) * 60f);
            }
            return scanYaw;
        }

        /// <summary>Where a holder looks for one coming up: at them when close, else where they will come
        /// over the zone's edge, so the view is there first.</summary>
        Vector3 EdgeToward(Vector3 eye, Vector3 them)
        {
            Vector3 c = match.Center, off = new Vector3(them.X - c.X, 0f, them.Z - c.Z);
            float d = UMath.Magnitude(off), reach = UMath.Min(match.HalfSize.X, match.HalfSize.Z);
            if (d <= reach + 4f || Flat(eye, them) < 8f)
                return them;
            Vector3 rim = UMath.Add(new Vector3(c.X, 0f, c.Z), UMath.Mul(off, reach / d));
            float ground = nav.GroundBelow(new Vector3(rim.X, eye.Y + 2f, rim.Z));
            return new Vector3(rim.X, ground > float.MinValue ? ground + 1f : them.Y, rim.Z);
        }

        // Holding with shots left: one coming up near the zone, in sight, is shot at from the spot (Luca's plan);
        // worth about as much as the hold itself, which it keeps.
        const float Snipe = 1.4f, SnipeNear = 0.6f, SnipeRange = 25f, SnipeBelow = 12f;

        bool Snipes(Vector3 at, Vector3 there, float d)
            => shotsLeft > 0 && d > 4.5f && d < SnipeRange && there.Y > match.Center.Y - match.HalfSize.Y - SnipeBelow
               && nav.InSight(UMath.Add(at, new Vector3(0f, 0.6f, 0f)), there);

        const float RecoverTurn = 5f;

        // In the zone, one this close in it or at its edge is fought off, however hurt: standing by only waits
        // for the knock (holders never swung back before).
        const float DefendRange = 4f, Defend = 0.2f, DefendEdge = 1.5f;

        // Holding, the nearest opponent this close is looked at and swung at when in reach, as a player on guard.
        const float GuardRange = 3.5f;

        int Guarded(WorldSnapshot view, Vector3 at)
        {
            int best = -1;
            float bestD = UMath.Min(GuardRange, settings.EngageRange);
            for (int i = 0; i < view.Count; i++)
            {
                BodyState s = view.StateAt(i);
                if (view.BodyAt(i).NetId == netId || view.BodyAt(i).NetId == 0 || team != 0 && s.Team == team)
                    continue;
                float d = Flat(at, Ahead(s.Motor.Position, s.Motor.Velocity, Reaction * Dt));
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        bool Threatens(Vector3 there)
            => UMath.Abs(there.X - match.Center.X) < match.HalfSize.X + DefendEdge
               && UMath.Abs(there.Z - match.Center.Z) < match.HalfSize.Z + DefendEdge
               && UMath.Abs(there.Y - match.Center.Y) < match.HalfSize.Y + DefendEdge;

        // A zone a step wide (TheLadder's top block) is held standing: a strafe, run or jump steps out of it.
        const float TightZone = 1.5f;
        const ushort Standing = 1 << (int)Maneuver.Walk | 1 << (int)Maneuver.Feint | 1 << (int)Maneuver.Crouch
                                | 1 << (int)Maneuver.Strike;

        bool TightHold(Vector3 at)
            => match != null && intent is BotIntent.Hold or BotIntent.Attack && match.Contains(at)
               && UMath.Min(match.HalfSize.X, match.HalfSize.Z) < TightZone;

        // 1, 2, 3, Free. A catcher hunting for its first catch goes for the weaker runner a little more.
        const float Weakest = 0.4f;
        // A fleeing runner jumps away (with a double jump) when the catcher comes this close, at most this often.
        const float EscapeReach = 4f;
        const int EscapeEvery = 60;
        // A runner lurks this far out from the zone's middle while the catcher guards it.
        const float LurkRange = 14f;
        bool lurking;

        /// <summary>Whether this runner's count in the zone ends before any catcher outside can come in
        /// (the count as seen is Reaction ticks old; the catcher sprints straight).</summary>
        bool Finishes(WorldSnapshot view, int self, Vector3 at)
        {
            BodyState me = view.StateAt(self);
            int need = ((FreeRole)me.Role == FreeRole.Rescuing ? HillMatch.RescueTicks : HillMatch.FreeTicks) - me.RoleTicks - Reaction;
            for (int i = 0; i < view.Count; i++)
            {
                BodyState s = view.StateAt(i);
                if ((FreeRole)s.Role != FreeRole.Catcher)
                    continue;
                Vector3 there = Ahead(s.Motor.Position, s.Motor.Velocity, Reaction * Dt);
                if (match.Contains(there))
                    return false;
                if (UMath.Max(0f, Flat(at, there) - 1f) / (body.SprintSpeed * tuning.Speed) / Dt < need)
                    return false;
            }
            return true;
        }

        /// <summary>How much going in is worth for a runner while a catcher guards: a lot when every catcher
        /// would come back later than this runner is in and counted, some when the one inside is busy close
        /// with another; else nothing (it lurks).</summary>
        float Opening(WorldSnapshot view, int self, Vector3 at)
        {
            float speed = body.SprintSpeed * tuning.Speed;
            float mine = UMath.Max(0f, Flat(at, match.Center) - UMath.Min(match.HalfSize.X, match.HalfSize.Z)) * 1.5f / speed
                         + HillMatch.FreeTicks * Dt;
            float worth = 1.6f;
            for (int i = 0; i < view.Count; i++)
            {
                BodyState c = view.StateAt(i);
                if ((FreeRole)c.Role != FreeRole.Catcher)
                    continue;
                Vector3 there = Ahead(c.Motor.Position, c.Motor.Velocity, Reaction * Dt);
                float back = match.Contains(there) ? 0f : Flat(there, match.Center) / speed;
                if (back > mine)
                    continue;
                worth = UMath.Min(worth, Engaged(view, i, self) ? 1.2f : 0f);
            }
            return worth;
        }

        // A catcher with another runner than this one close by it.
        const float EngagedRange = 6f;
        static bool Engaged(WorldSnapshot view, int catcher, int self)
        {
            Vector3 at = view.StateAt(catcher).Motor.Position;
            for (int k = 0; k < view.Count; k++)
                if (k != self && k != catcher && (FreeRole)view.StateAt(k).Role is FreeRole.Runner or FreeRole.Rescuing
                    && UMath.Magnitude(UMath.Sub(view.StateAt(k).Motor.Position, at)) < EngagedRange)
                    return true;
            return false;
        }

        // A lurking runner's place: on its own side of the zone, LurkRange out (8 m off a wide zone's edge), on the grid.
        Vector3 Lurk(Vector3 at)
        {
            Vector3 c = match.Center, off = new Vector3(at.X - c.X, 0f, at.Z - c.Z);
            float d = UMath.Magnitude(off), range = UMath.Max(LurkRange, UMath.Max(match.HalfSize.X, match.HalfSize.Z) + 8f);
            if (d < 1e-3f || d < range + 3f && d > range - 3f)
                return Feet(at);
            Vector3 p = new Vector3(c.X + off.X * range / d, Feet(at).Y + (range - d) * 0.5f, c.Z + off.Z * range / d);
            return nav.Nearest(p, out Vector3 onMesh) ? onMesh : Feet(at);
        }

        /// <summary>Where a runner flees: of eight ways 10 m out, the one furthest from the catcher, better out
        /// of its sight (behind the hill), on the grid.</summary>
        Vector3 Refuge(Vector3 at, Vector3 from)
        {
            Vector3 best = Feet(at);
            float bestScore = float.MinValue;
            Vector3 eye = UMath.Add(from, new Vector3(0f, 0.6f, 0f));
            for (int k = 0; k < 8; k++)
            {
                float a = k * (MathF.PI / 4f);
                Vector3 p = Feet(new Vector3(at.X + 10f * UMath.Cos(a), at.Y, at.Z + 10f * UMath.Sin(a)));
                if (!nav.Nearest(p, out Vector3 onMesh))
                    continue;
                Vector3 centre = new Vector3(onMesh.X, onMesh.Y + body.StandHeight * 0.5f, onMesh.Z);
                float score = Flat(centre, from) + (nav.InSight(eye, centre) ? 0f : 6f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = onMesh;
                }
            }
            return best;
        }

        // Walking on and not getting anywhere (a body pressed under a ramp keeps its speed, so the speed cannot
        // tell): the floor it walks for is made dearer and it steps aside for a while, one side then the other.
        const int BlockedTicks = 40, DodgeTicks = 24;
        const float BlockedCost = 4f;
        Vector3 stillAt;
        int stillSince, dodgeUntil = -1, dodgeSide = 1;

        bool Blocked(int u, in BodyState me, bool walking)
        {
            if (!walking || Flat(me.Motor.Position, stillAt) > 0.3f)
            {
                stillAt = me.Motor.Position;
                stillSince = u;
            }
            else if (u - stillSince >= BlockedTicks)
            {
                if (corners.Count >= 2)
                    nav.Grid.Stuck(corners[1].Position, BlockedCost, memory);
                dodgeSide = -dodgeSide;
                dodgeUntil = u + DodgeTicks;
                stillSince = u;
            }
            return u < dodgeUntil;
        }

        // How close a target draws a run at it, and how many ticks before the cane is ready the run starts.
        const float ChargeRange = 5f;
        const int ChargeLead = 6;

        // Keys stay at least this long (a tap shorter reads as a bot), and never flip straight to the opposite.
        const int KeyHold = 4;

        // Every path's keys on the ground, last: a state stays two ticks (a single 25 ms tap is no thumb's) and a key
        // lets go before its opposite. In the air and on a jump's tick the pilot's keys pass as they are.
        const int ThumbHold = 2;
        const float SettleSlack = 1f;
        Vector3 settledAt = new Vector3(float.NaN);
        Vector2 thumbKeys;
        int thumbSince;

        Vector2 Thumb(int u, Vector2 want, bool free)
        {
            if (want == thumbKeys)
                return want;
            if (!free)
            {
                if (u - thumbSince < ThumbHold)
                    return thumbKeys;
                want = new Vector2(want.X * thumbKeys.X < 0f ? 0f : want.X, want.Y * thumbKeys.Y < 0f ? 0f : want.Y);
            }
            thumbKeys = want;
            thumbSince = u;
            return want;
        }

        Vector2 HeldKeys(int u, Vector2 want)
        {
            if (want == heldKeys)
                return heldKeys;
            if (u - keysSince < KeyHold)
                return heldKeys;
            // An axis turning round lets go first.
            float x = want.X * heldKeys.X < 0f ? 0f : want.X, y = want.Y * heldKeys.Y < 0f ? 0f : want.Y;
            heldKeys = new Vector2(x, y);
            keysSince = u;
            return heldKeys;
        }
    }
}
