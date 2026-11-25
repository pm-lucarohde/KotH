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
        /// <summary>"bots duel SECONDS": bots against a fixed opponent, a scripted player holding the zone at a set
        /// percent (KOTH_DUEL_PERCENT, 40), fighting back unless KOTH_DUEL_FIGHT=0. Counts how long the bots
        /// (KOTH_DUEL_BOTS, 3) take to knock it out of the zone; it then starts again in the middle.</summary>
        public int Duel = 0;

        int DuelRun(int seconds)
        {
            string map = Environment.GetEnvironmentVariable("KOTH_ROUNDS_MAP") ?? Maps.Default;
            int count = int.Parse(Environment.GetEnvironmentVariable("KOTH_DUEL_BOTS") ?? "3");
            int seed = int.Parse(Environment.GetEnvironmentVariable("KOTH_ROUNDS_SEED") ?? "1");
            float percent = float.Parse(Environment.GetEnvironmentVariable("KOTH_DUEL_PERCENT") ?? "40", System.Globalization.CultureInfo.InvariantCulture);
            bool fight = Environment.GetEnvironmentVariable("KOTH_DUEL_FIGHT") != "0";
            using var world = new HeadlessWorld();
            world.LoadArena(map);
            HillMatch match = world.StartMatch();
            match.Seed = (uint)seed;
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
            var holder = new Holder(match, bots, fight, (uint)seed * 2654435761u | 1u) { NetId = NetWorld.FirstPlayerId };
            Vector3 middle = match.Center - new Vector3(0f, match.HalfSize.Y * 0.5f, 0f);
            Entity he = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, middle, Quaternion.Identity, holder, new DamagePercent());
            he.Name = "Holder";
            holder.Motor = he.Get<PlayerMotor>();
            var damage = he.Get<DamagePercent>();
            var melee = he.Get<MeleeAttack>();
            damage.Restore(percent);
            Log($"[duel] map {map}, {count} bots against a holder at {percent:F0} %, {(fight ? "fighting back" : "passive")}, seed {seed}");

            var ringPos = new Vector3[64];
            var ringVel = new Vector3[64];
            var botSwings = new int[count];
            var botHits = new int[count];
            long mPred = 0, mAim = 0, mFar = 0, mHit = 0, mOther = 0;
            double predErr = 0, eyeErr = 0, centreErr = 0, carrySum = 0;
            var from = new int[4];
            var times = new List<float>();
            int since = 0, outside = 0, lastHit = damage.LastHitTick, hitsTaken = 0, episodeHits = 0;
            bool started = false;
            var hitsPer = new List<int>();
            long ticks = seconds * 40L;
            for (long t = 0; t < ticks; t++)
            {
                world.Simulation.Tick();
                ringPos[t & 63] = holder.Motor.BodyCenter;
                ringVel[t & 63] = holder.Motor.Velocity;
                for (int i = 0; i < count && t > 16; i++)
                {
                    var bm = bots[i].Entity.Get<MeleeAttack>();
                    if (bm.SwingCount == botSwings[i])
                        continue;
                    botSwings[i] = bm.SwingCount;
                    bool hit = bm.HitCount != botHits[i];
                    botHits[i] = bm.HitCount;
                    BotBrain mind = director.BrainOf(bots[i].NetId);
                    if (mind.Target != holder.NetId)
                        continue;
                    if (hit)
                    {
                        mHit++;
                        Vector3 line = holder.Motor.Position - bots[i].Motor.Position;
                        line.Y = 0f;
                        if (line.LengthSquared() > 1e-6f)
                            carrySum += Vector3.Dot(new Vector3(bots[i].Motor.Velocity.X, 0f, bots[i].Motor.Velocity.Z), Vector3.Normalize(line));
                        continue;
                    }
                    Vector3 eye = bots[i].Motor.PreviousEyePosition, real = ringPos[(t - 1) & 63];
                    eyeErr += Vector3.Distance(eye, mind.SwingEye);
                    centreErr += Vector3.Distance(real, mind.SwingCentre);
                    from[mind.SwingFrom]++;
                    Vector3 pred = ringPos[(t - 9) & 63] + ringVel[(t - 9) & 63] * 0.2f;
                    predErr += Vector3.Distance(real, pred);
                    Vector3 to = real - eye, tp = pred - eye;
                    float flat = MathF.Sqrt(to.X * to.X + to.Z * to.Z);
                    float yawReal = MathF.Atan2(-to.X, -to.Z) * 180f / MathF.PI, yawPred = MathF.Atan2(-tp.X, -tp.Z) * 180f / MathF.PI;
                    float cone = MathF.Atan2(0.32f, MathF.Max(flat, 0.5f)) * 180f / MathF.PI, y = mind.LastInput.Yaw;
                    if (to.Length() > 3.6f)
                        mFar++;
                    else if (MathF.Abs(MathF.IEEERemainder(yawPred - y, 360f)) < cone)
                        mPred++;
                    else if (MathF.Abs(MathF.IEEERemainder(yawReal - y, 360f)) >= cone)
                        mAim++;
                    else
                        mOther++;
                }
                Vector3 at = holder.Motor.Position;
                bool inside = match.Contains(at);
                // The clock runs from the first bot in reach: the climb is "bots climb"'s.
                if (!started)
                {
                    started = bots.Any(b => Vector3.Distance(b.Motor.Position, at) < 8f);
                    if (!inside || t % 400 == 0)
                        holder.Motor.Teleport(middle);
                    since = 0;
                    damage.Restore(percent);
                    continue;
                }
                since++;
                if (damage.LastHitTick != lastHit)
                {
                    lastHit = damage.LastHitTick;
                    hitsTaken++;
                    episodeHits++;
                }
                outside = inside ? 0 : outside + 1;
                if (outside >= 40)
                {
                    times.Add((since - 40) / 40f);
                    hitsPer.Add(episodeHits);
                    holder.Motor.Teleport(middle);
                    damage.Restore(percent);
                    since = outside = episodeHits = 0;
                }
            }
            director.Dispose();
            Log($"[duel] bot swings at the holder: hit {mHit}, missed: prediction {mPred} (mean error {predErr / Math.Max(1, mPred + mAim + mFar + mOther):F2} m), " +
                $"aim {mAim}, far {mFar}, other {mOther}; the brain's error: own eye {eyeErr / Math.Max(1, mPred + mAim + mFar + mOther):F2} m, " +
                $"target {centreErr / Math.Max(1, mPred + mAim + mFar + mOther):F2} m; from steer {from[1]}, passing {from[2]}; run-in at hits {carrySum / Math.Max(1, mHit):F2} m/s");
            times.Sort();
            float median = times.Count > 0 ? times[times.Count / 2] : float.NaN;
            float played = ticks / 40f;
            Log($"[duel] knocked out {times.Count} times, median {median:F1} s, mean {(times.Count > 0 ? times.Average() : 0):F1} s, " +
                $"hits taken {hitsTaken} ({hitsTaken / (played / 60f):F1}/min), per knock-out {(hitsPer.Count > 0 ? hitsPer.Average() : 0):F1}; " +
                $"holder swings {melee.SwingCount}, landed {melee.HitCount}; bots swing {bots.Sum(b => b.Entity.Get<MeleeAttack>().SwingCount)}, " +
                $"landed {bots.Sum(b => b.Entity.Get<MeleeAttack>().HitCount) * 100 / Math.Max(1, bots.Sum(b => b.Entity.Get<MeleeAttack>().SwingCount))} %");
            return 0;
        }

        /// <summary>The fixed opponent: wanders in the zone as a player holding it would, back to the middle when
        /// near the rim or out, and, fighting, swings at the nearest bot in reach, aimed at where it is.</summary>
        sealed class Holder : SimulationBody
        {
            readonly HillMatch match;
            readonly List<BotBody> bots;
            readonly bool fight;
            uint rng;
            int until;
            float walkYaw, yaw;
            bool walking;
            int tick;

            public PlayerMotor Motor;

            public Holder(HillMatch match, List<BotBody> bots, bool fight, uint seed)
            {
                this.match = match;
                this.bots = bots;
                this.fight = fight;
                rng = seed;
            }

            public override MoveInput ReadInput()
            {
                tick++;
                if (Motor == null)
                    return default;
                Vector3 at = Motor.Position, c = match.Center;
                float rim = MathF.Min(match.HalfSize.X, match.HalfSize.Z);
                float fromMiddle = MathF.Sqrt((at.X - c.X) * (at.X - c.X) + (at.Z - c.Z) * (at.Z - c.Z));
                if (fromMiddle > 0.6f * rim || !match.Contains(at))
                {
                    walkYaw = MathF.Atan2(-(c.X - at.X), -(c.Z - at.Z)) * (180f / MathF.PI);
                    walking = true;
                    until = tick + 10;
                }
                else if (tick >= until)
                {
                    walking = BotRandom.Unit(ref rng) > 0.3f;
                    walkYaw = BotRandom.Unit(ref rng) * 360f;
                    until = tick + 20 + (int)(BotRandom.Unit(ref rng) * 40f);
                }

                // Face the nearest bot within 15 m, as a player watches who comes; swing at it in reach.
                BotBody near = null;
                float best = 15f;
                foreach (BotBody b in bots)
                {
                    float d = Vector3.Distance(b.Motor.BodyCenter, Motor.EyePosition);
                    if (d < best)
                    {
                        best = d;
                        near = b;
                    }
                }
                float pitch = 0f;
                var input = new MoveInput();
                if (near != null)
                {
                    Vector3 to = near.Motor.BodyCenter - Motor.EyePosition;
                    yaw = MathF.Atan2(-to.X, -to.Z) * (180f / MathF.PI);
                    pitch = MathF.Atan2(to.Y, MathF.Sqrt(to.X * to.X + to.Z * to.Z)) * (180f / MathF.PI);
                    if (fight && best < 3.3f)
                        input.AttackPresses = 1;
                }
                else
                    yaw = walkYaw;
                float rel = (walkYaw - yaw) * (MathF.PI / 180f);
                input.Yaw = yaw;
                input.Pitch = pitch;
                input.Move = walking ? new Vector2(MathF.Round(-MathF.Sin(rel)), MathF.Round(MathF.Cos(rel))) : Vector2.Zero;
                return input;
            }
        }
    }
}
