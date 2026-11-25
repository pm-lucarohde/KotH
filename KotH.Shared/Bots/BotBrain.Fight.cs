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
                                   1f + BotRandom.Signed(ref r, 0.3f), 16 + (int)(BotRandom.Unit(ref r) * 32f));
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

        public Vector3 SwingEye, SwingCentre;   // probes: where the last swing thought itself and its target
        public int SwingFrom;

        // Hold over attack: alone in the zone it is to be taken (3 s), held it is to be kept.
        const float HoldAlone = 1.6f, HoldKept = 1.25f, HoldShared = 0.4f;

        // Two already at a target are enough (unless it is in the zone); one already at it is a fight to join.
        const float Crowded = 0.5f, ThirdParty = 0.25f, LeaderBonus = 0.35f, Busy = 3f;

        // A target newly preferred while the old one has not held attention for Focus ticks counts this much less.
        const float Distracted = 0.7f;

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
            else if (near >= 2 && !isHolder)
                score *= Crowded;
            if (bestScore > 0)
                score += LeaderBonus * s.ScoreTicks / (float)bestScore;
            score *= persona.Aggression * (1f - 0.4f * persona.Caution * UMath.Min(1f, ownDamage / 150f));
            int id = view.BodyAt(i).NetId;
            if (intent == BotIntent.Attack && target != 0 && target != id && thinkTick - targetSince < persona.Focus)
                score *= Distracted;
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

        // How close a target draws a run at it, and how many ticks before the cane is ready the run starts.
        const float ChargeRange = 5f;
        const int ChargeLead = 6;

        // Keys stay at least this long (a tap shorter reads as a bot), and never flip straight to the opposite.
        const int KeyHold = 4;

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
