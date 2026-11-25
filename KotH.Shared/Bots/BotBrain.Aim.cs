using System;
using KotH.Shared.Mathematics;

namespace KotH.Shared.Bots
{
    /// <summary>The view as a hand on a mouse (Luca, 25.11.2026: the bot's own view must not shake or stutter).
    /// Every caller only says where it wants to look; the hand gets there with a bell of speed, follows a
    /// moving goal and misses by a slow drift instead of a new random error each tick.</summary>
    public sealed partial class BotBrain
    {
        // The most the turn rate changes a tick at TurnRate (deg/tick²): about 0.2 s for a quarter turn, a quick
        // flick, yet under what reads as a jerk (8).
        const float HandAccel = 7f;

        // Braking plans with half the hand's grip, so a turn ends without overshooting; then the last share per tick.
        const float BrakeShare = 0.5f, Settle = 0.5f;

        // The view a little down on the way ahead; how far off a shot may still go out (degrees).
        const float WalkPitch = -5f, ShotCone = 1.2f;

        float lastWantYaw, lastWantPitch;
        float missYaw, missPitch, missYawGoal, missPitchGoal;  // the drift, -1..1 of the aim error
        int missNext;
        uint missRng;                // its own dice, so the view never changes what the bot decides
        float jamYaw;
        int glanceUntil = -1;
        float glanceYaw, glancePitch;

        /// <summary>The next turn rate: the goal's own motion followed, then towards the goal no faster than it
        /// can still brake, with at most accel more or less than the last rate.</summary>
        static float Hand(float rate, float error, float follow, float maxRate, float accel)
        {
            if (UMath.Abs(follow) > 8f)
                follow = 0f;   // the goal jumped elsewhere: a new turn, not a target moving
            float magnitude = UMath.Abs(error);
            float closing = UMath.Min(UMath.Min(UMath.Sqrt(2f * accel * BrakeShare * magnitude), magnitude * Settle), maxRate);
            float want = UMath.Clamp(follow + (error < 0f ? -closing : closing), -maxRate, maxRate);
            return rate + UMath.Clamp(want - rate, -accel, accel);
        }

        // The miss drifts to a new goal every 0.4 to 1 s, as a hand that never quite holds still.
        void Drift(int u)
        {
            if (u >= missNext)
            {
                missYawGoal = BotRandom.Signed(ref missRng, 1f);
                missPitchGoal = BotRandom.Signed(ref missRng, 0.6f);
                missNext = u + 16 + (int)(BotRandom.Unit(ref missRng) * 24f);
            }
            missYaw += (missYawGoal - missYaw) * 0.12f;
            missPitch += (missPitchGoal - missPitch) * 0.12f;
        }
    }
}
