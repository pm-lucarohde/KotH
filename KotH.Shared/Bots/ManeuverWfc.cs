using System;

namespace KotH.Shared.Bots
{
    /// <summary>What a bot does for one slot of a few ticks.</summary>
    public enum Maneuver : byte
    {
        Walk, Sprint, StrafeLeft, StrafeRight, Feint, Jump, DoubleJump, Slide, Crouch, Strike,
    }

    /// <summary>
    /// Wave function collapse in one dimension: a row of slots, each still
    /// open to a set of maneuvers, collapsed one at a time where the fewest
    /// remain, each choice cutting down its neighbours by the rules below.
    /// The rules are the motor's: a slide needs a sprint before it, a double
    /// jump a jump, a swing its cooldown; a jump may land into a slide. Chance comes from the bot's xorshift.
    /// </summary>
    public sealed class ManeuverWfc
    {
        public const int Count = 10;
        public const ushort All = (1 << Count) - 1;

        // A neighbour already decided pulls its own kind this much harder:
        // people keep doing what they do instead of switching every slot.
        const float Stick = 3f;

        static readonly ushort[] Next = BuildNext();

        static ushort Bit(Maneuver m) => (ushort)(1 << (int)m);

        static ushort Bits(params Maneuver[] ms)
        {
            ushort b = 0;
            foreach (Maneuver m in ms)
                b |= Bit(m);
            return b;
        }

        /// <summary>Which maneuvers may follow each one.</summary>
        static ushort[] BuildNext()
        {
            ushort air = Bits(Maneuver.Walk, Maneuver.Sprint, Maneuver.StrafeLeft, Maneuver.StrafeRight, Maneuver.Strike);
            ushort ground = (ushort)(All & ~Bit(Maneuver.Slide) & ~Bit(Maneuver.DoubleJump));
            var next = new ushort[Count];
            for (int i = 0; i < Count; i++)
                next[i] = ground;
            next[(int)Maneuver.Sprint] |= Bit(Maneuver.Slide);
            // Crouch held through a landing slides on: jump, then slide.
            next[(int)Maneuver.Jump] = (ushort)(air | Bit(Maneuver.DoubleJump) | Bit(Maneuver.Slide));
            next[(int)Maneuver.DoubleJump] = (ushort)(air | Bit(Maneuver.Slide));
            next[(int)Maneuver.Slide] = Bits(Maneuver.Sprint, Maneuver.Walk, Maneuver.Jump, Maneuver.Strike, Maneuver.Feint);
            return next;
        }

        readonly ushort[] domain;
        readonly Maneuver[] result;

        // Slots that must lie between two of the same: the swing's cooldown
        // and the slide's 0,5 s, in slots.
        readonly int strikeGap, slideGap;

        public ManeuverWfc(int slots, int slotTicks, int strikeCooldownTicks)
        {
            domain = new ushort[slots];
            result = new Maneuver[slots];
            strikeGap = (strikeCooldownTicks + slotTicks - 1) / slotTicks + 1;
            slideGap = (20 + slotTicks - 1) / slotTicks + 1;
        }

        public int Slots => domain.Length;

        /// <summary>
        /// Fills a plan. Allowed[i] narrows slot i beforehand (0 for open),
        /// after is the maneuver running now, weights give each maneuver's
        /// pull, doubled and more next to a decided slot of the same kind.
        /// A contradiction starts over without the narrowing; the
        /// plan always comes out complete.
        /// </summary>
        public Maneuver[] Collapse(ReadOnlySpan<ushort> allowed, Maneuver after, ReadOnlySpan<float> weights, ref uint rng)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                for (int i = 0; i < domain.Length; i++)
                {
                    ushort a = i < allowed.Length && attempt < 2 && allowed[i] != 0 ? allowed[i] : All;
                    domain[i] = (ushort)(a & Weighted(weights));
                }
                domain[0] &= Next[(int)after];
                if (Propagate() && Run(weights, ref rng))
                    return result;
            }

            // Nothing fits even unconstrained: walk.
            Array.Fill(result, Maneuver.Walk);
            return result;
        }

        static ushort Weighted(ReadOnlySpan<float> weights)
        {
            ushort b = 0;
            for (int i = 0; i < Count && i < weights.Length; i++)
                if (weights[i] > 0f)
                    b |= (ushort)(1 << i);
            return b == 0 ? Bit(Maneuver.Walk) : b;
        }

        bool Run(ReadOnlySpan<float> weights, ref uint rng)
        {
            while (true)
            {
                // Every decided slot keeps its own kind out of its reach first.
                for (int i = 0; i < domain.Length; i++)
                    if (System.Numerics.BitOperations.PopCount(domain[i]) == 1)
                        Space(i, (Maneuver)System.Numerics.BitOperations.TrailingZeroCount(domain[i]));
                if (!Propagate())
                    return false;

                // Lowest entropy first: the open slot with the fewest choices,
                // the earliest on a tie, so the order never depends on chance.
                int pick = -1, fewest = int.MaxValue;
                for (int i = 0; i < domain.Length; i++)
                {
                    int n = System.Numerics.BitOperations.PopCount(domain[i]);
                    if (n > 1 && n < fewest)
                    {
                        fewest = n;
                        pick = i;
                    }
                }
                if (pick < 0)
                    break;
                domain[pick] = Bit(Choose(pick, weights, ref rng));
            }

            for (int i = 0; i < domain.Length; i++)
                result[i] = (Maneuver)System.Numerics.BitOperations.TrailingZeroCount(domain[i]);
            return true;
        }

        Maneuver Choose(int at, ReadOnlySpan<float> weights, ref uint rng)
        {
            ushort options = domain[at];
            Span<float> pull = stackalloc float[Count];
            float total = 0f;
            for (int i = 0; i < Count; i++)
            {
                if ((options & (1 << i)) == 0)
                    continue;
                pull[i] = weights[i];
                if (at > 0 && domain[at - 1] == 1 << i || at + 1 < domain.Length && domain[at + 1] == 1 << i)
                    pull[i] *= Stick;
                total += pull[i];
            }
            float roll = BotRandom.Unit(ref rng) * total;
            for (int i = 0; i < Count; i++)
            {
                if ((options & (1 << i)) == 0)
                    continue;
                roll -= pull[i];
                if (roll < 0f)
                    return (Maneuver)i;
            }
            return (Maneuver)(31 - System.Numerics.BitOperations.LeadingZeroCount((uint)options));
        }

        // The rules that reach past a neighbour: no second swing or slide too
        // soon. A neighbour already decided the same way empties and fails.
        void Space(int at, Maneuver m)
        {
            int gap = m == Maneuver.Strike ? strikeGap : m == Maneuver.Slide ? slideGap : 0;
            for (int d = 1; d < gap; d++)
            {
                if (at - d >= 0)
                    domain[at - d] &= (ushort)~Bit(m);
                if (at + d < domain.Length)
                    domain[at + d] &= (ushort)~Bit(m);
            }
        }

        bool Valid()
        {
            for (int i = 0; i < domain.Length; i++)
                if (domain[i] == 0)
                    return false;
            return true;
        }

        /// <summary>Arc consistency along the row, both ways, until nothing changes.</summary>
        bool Propagate()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i + 1 < domain.Length; i++)
                {
                    ushort reach = 0;
                    for (int m = 0; m < Count; m++)
                        if ((domain[i] & (1 << m)) != 0)
                            reach |= Next[m];
                    ushort narrowed = (ushort)(domain[i + 1] & reach);
                    if (narrowed != domain[i + 1])
                    {
                        domain[i + 1] = narrowed;
                        changed = true;
                    }
                }
                for (int i = domain.Length - 1; i > 0; i--)
                {
                    ushort keep = 0;
                    for (int m = 0; m < Count; m++)
                        if ((domain[i - 1] & (1 << m)) != 0 && (Next[m] & domain[i]) != 0)
                            keep |= (ushort)(1 << m);
                    if (keep != domain[i - 1])
                    {
                        domain[i - 1] = keep;
                        changed = true;
                    }
                }
                if (!Valid())
                    return false;
            }
            return true;
        }
    }

    /// <summary>The bots' seeded xorshift; never a clock, never System.Random.</summary>
    public static class BotRandom
    {
        public static uint Seed(int seed, int netId)
            => ((uint)seed * 2654435761u ^ (uint)netId * 40503u) | 1u;

        public static uint Next(ref uint rng)
        {
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            return rng;
        }

        /// <summary>Evenly in [0, 1), from 24 bits so the float is exact.</summary>
        public static float Unit(ref uint rng) => (Next(ref rng) >> 8) * (1f / 16777216f);

        /// <summary>Evenly in [-range, range).</summary>
        public static float Signed(ref uint rng, float range) => (Unit(ref rng) * 2f - 1f) * range;
    }
}
