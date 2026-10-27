using System;
using System.Collections.Generic;
using System.Numerics;

namespace KotH.Tools;

/// <summary>
/// numpy's default_rng(seed): SeedSequence feeding PCG64 (XSL-RR), as numpy 2.5 draws
/// its doubles and standard normals (ziggurat), so a tool gets the very same numbers.
/// </summary>
public sealed class NumPyRandom
{
    static readonly UInt128 Multiplier = new(2549297995355413924UL, 4865540595714422341UL);
    UInt128 state, increment;

    public NumPyRandom(uint seed)
    {
        ulong[] words = SeedSequence([seed], 4);
        var initState = new UInt128(words[0], words[1]);
        var initSeq = new UInt128(words[2], words[3]);
        state = 0;
        increment = (initSeq << 1) | 1;
        Step();
        state += initState;
        Step();
    }

    void Step() => state = state * Multiplier + increment;

    public ulong NextUInt64()
    {
        Step();
        ulong high = (ulong)(state >> 64), low = (ulong)state;
        return BitOperations.RotateRight(high ^ low, (int)(high >> 58));
    }

    /// <summary>rng.random(): 53 random bits in [0, 1).</summary>
    public double Random() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    public double[] Random(int count)
    {
        var values = new double[count];
        for (int i = 0; i < count; i++)
            values[i] = Random();
        return values;
    }

    /// <summary>rng.standard_normal(): numpy's ziggurat.</summary>
    public double StandardNormal()
    {
        while (true)
        {
            ulong r = NextUInt64();
            int idx = (int)(r & 0xff);
            r >>= 8;
            bool negative = (r & 1) != 0;
            ulong rabs = (r >> 1) & 0x000fffffffffffffUL;
            double x = rabs * ZigguratTables.Wi[idx];
            if (negative)
                x = -x;
            if (rabs < ZigguratTables.Ki[idx])
                return x;
            if (idx == 0)
            {
                while (true)
                {
                    double xx = -ZigguratTables.InvR * Libm.log1p(-Random());
                    double yy = -Libm.log1p(-Random());
                    if (yy + yy > xx * xx)
                        return ((rabs >> 8) & 1) != 0 ? -(ZigguratTables.R + xx) : ZigguratTables.R + xx;
                }
            }
            if ((ZigguratTables.Fi[idx - 1] - ZigguratTables.Fi[idx]) * Random() + ZigguratTables.Fi[idx] < Libm.exp(-0.5 * x * x))
                return x;
        }
    }

    // numpy.random.SeedSequence(entropy).generate_state(n, uint64), pool size 4, no spawn key.
    static ulong[] SeedSequence(uint[] entropy, int words)
    {
        const uint InitA = 0x43b0d7e5, MultA = 0x931e8875, InitB = 0x8b51f9dd, MultB = 0x58f38ded;
        const uint MixL = 0xca01f9dd, MixR = 0x4973f715;
        uint hash = InitA;
        uint HashMix(uint value)
        {
            value ^= hash;
            hash *= MultA;
            value *= hash;
            return value ^ (value >> 16);
        }
        static uint Mix(uint x, uint y)
        {
            uint result = MixL * x - MixR * y;
            return result ^ (result >> 16);
        }
        var pool = new uint[4];
        for (int i = 0; i < pool.Length; i++)
            pool[i] = HashMix(i < entropy.Length ? entropy[i] : 0);
        for (int src = 0; src < pool.Length; src++)
            for (int dst = 0; dst < pool.Length; dst++)
                if (src != dst)
                    pool[dst] = Mix(pool[dst], HashMix(pool[src]));
        for (int src = pool.Length; src < entropy.Length; src++)
            for (int dst = 0; dst < pool.Length; dst++)
                pool[dst] = Mix(pool[dst], HashMix(entropy[src]));

        var state = new uint[2 * words];
        uint constant = InitB;
        for (int i = 0; i < state.Length; i++)
        {
            uint value = pool[i % pool.Length] ^ constant;
            constant *= MultB;
            value *= constant;
            state[i] = value ^ (value >> 16);
        }
        var result = new ulong[words];
        for (int i = 0; i < words; i++)
            result[i] = state[2 * i] | ((ulong)state[2 * i + 1] << 32);
        return result;
    }
}

/// <summary>
/// numpy.fft for power-of-two lengths: pocketfft's cfftp (numpy 2.5.4), pass for pass,
/// so the sums round as there. Lengths with other factors are not needed and refused.
/// </summary>
public sealed class NumPyFft
{
    readonly int length;
    readonly List<(int Factor, Complex[] Twiddle)> passes = [];

    public NumPyFft(int length)
    {
        if (length < 2 || !BitOperations.IsPow2(length))
            throw new NotSupportedException($"FFT der Länge {length}: nur Zweierpotenzen");
        this.length = length;
        // cfftp::factorize: eights, then fours, a two goes to the front.
        var factors = new List<int>();
        int rest = length;
        while ((rest & 7) == 0) { factors.Add(8); rest >>= 3; }
        while ((rest & 3) == 0) { factors.Add(4); rest >>= 2; }
        if ((rest & 1) == 0)
        {
            rest >>= 1;
            factors.Add(2);
            (factors[0], factors[^1]) = (factors[^1], factors[0]);
        }
        var twiddle = new SinCos2PiByN(length);
        int l1 = 1;
        foreach (int ip in factors)
        {
            int ido = length / (l1 * ip);
            var tw = new Complex[(ip - 1) * Math.Max(ido - 1, 0)];
            for (int j = 1; j < ip; j++)
                for (int i = 1; i < ido; i++)
                    tw[(j - 1) * (ido - 1) + i - 1] = twiddle[j * l1 * i];
            passes.Add((ip, tw));
            l1 *= ip;
        }
    }

    /// <summary>In place; fct scales the result (1/n for numpy's inverse).</summary>
    public void Exec(Complex[] c, double fct, bool forward)
    {
        var ch = new Complex[length];
        Complex[] p1 = c, p2 = ch;
        int l1 = 1;
        foreach (var (ip, tw) in passes)
        {
            int ido = length / (ip * l1);
            if (ip == 2) Pass2(ido, l1, p1, p2, tw, forward);
            else if (ip == 4) Pass4(ido, l1, p1, p2, tw, forward);
            else Pass8(ido, l1, p1, p2, tw, forward);
            (p1, p2) = (p2, p1);
            l1 *= ip;
        }
        if (p1 != c)
        {
            if (fct != 1.0)
                for (int i = 0; i < length; i++)
                    c[i] = new Complex(ch[i].Real * fct, ch[i].Imaginary * fct);
            else
                Array.Copy(p1, c, length);
        }
        else if (fct != 1.0)
            for (int i = 0; i < length; i++)
                c[i] = new Complex(c[i].Real * fct, c[i].Imaginary * fct);
    }

    // The complex helpers spelled out, each in pocketfft's order of operations.
    static Complex Add(Complex a, Complex b) => new(a.Real + b.Real, a.Imaginary + b.Imaginary);
    static Complex Sub(Complex a, Complex b) => new(a.Real - b.Real, a.Imaginary - b.Imaginary);

    static Complex SpecialMul(Complex v1, Complex v2, bool fwd) => fwd
        ? new(v1.Real * v2.Real + v1.Imaginary * v2.Imaginary, v1.Imaginary * v2.Real - v1.Real * v2.Imaginary)
        : new(v1.Real * v2.Real - v1.Imaginary * v2.Imaginary, v1.Real * v2.Imaginary + v1.Imaginary * v2.Real);

    static Complex RotX90(Complex a, bool fwd) => fwd ? new(a.Imaginary, -a.Real) : new(-a.Imaginary, a.Real);

    const double Hsqt2 = 0.707106781186547524400844362104849;

    static Complex RotX45(Complex a, bool fwd) => fwd
        ? new(Hsqt2 * (a.Real + a.Imaginary), Hsqt2 * (a.Imaginary - a.Real))
        : new(Hsqt2 * (a.Real - a.Imaginary), Hsqt2 * (a.Imaginary + a.Real));

    static Complex RotX135(Complex a, bool fwd) => fwd
        ? new(Hsqt2 * (a.Imaginary - a.Real), Hsqt2 * (-a.Real - a.Imaginary))
        : new(Hsqt2 * (-a.Real - a.Imaginary), Hsqt2 * (a.Real - a.Imaginary));

    static void Pass2(int ido, int l1, Complex[] cc, Complex[] ch, Complex[] wa, bool fwd)
    {
        int CH(int a, int b, int c) => a + ido * (b + l1 * c);
        int CC(int a, int b, int c) => a + ido * (b + 2 * c);
        for (int k = 0; k < l1; k++)
        {
            ch[CH(0, k, 0)] = Add(cc[CC(0, 0, k)], cc[CC(0, 1, k)]);
            ch[CH(0, k, 1)] = Sub(cc[CC(0, 0, k)], cc[CC(0, 1, k)]);
            for (int i = 1; i < ido; i++)
            {
                ch[CH(i, k, 0)] = Add(cc[CC(i, 0, k)], cc[CC(i, 1, k)]);
                ch[CH(i, k, 1)] = SpecialMul(Sub(cc[CC(i, 0, k)], cc[CC(i, 1, k)]), wa[i - 1], fwd);
            }
        }
    }

    static void Pass4(int ido, int l1, Complex[] cc, Complex[] ch, Complex[] wa, bool fwd) =>
        throw new NotSupportedException("FFT-Faktor 4 kommt bei Zweierpotenzen über 8 nicht vor");

    static void Pass8(int ido, int l1, Complex[] cc, Complex[] ch, Complex[] wa, bool fwd)
    {
        int CH(int a, int b, int c) => a + ido * (b + l1 * c);
        int CC(int a, int b, int c) => a + ido * (b + 8 * c);
        Complex WA(int x, int i) => wa[i - 1 + x * (ido - 1)];
        for (int k = 0; k < l1; k++)
        {
            {
                Complex a1 = Add(cc[CC(0, 1, k)], cc[CC(0, 5, k)]), a5 = Sub(cc[CC(0, 1, k)], cc[CC(0, 5, k)]);
                Complex a3 = Add(cc[CC(0, 3, k)], cc[CC(0, 7, k)]), a7 = Sub(cc[CC(0, 3, k)], cc[CC(0, 7, k)]);
                (a1, a3) = (Add(a1, a3), Sub(a1, a3));
                a3 = RotX90(a3, fwd);
                a7 = RotX90(a7, fwd);
                (a5, a7) = (Add(a5, a7), Sub(a5, a7));
                a5 = RotX45(a5, fwd);
                a7 = RotX135(a7, fwd);
                Complex a0 = Add(cc[CC(0, 0, k)], cc[CC(0, 4, k)]), a4 = Sub(cc[CC(0, 0, k)], cc[CC(0, 4, k)]);
                Complex a2 = Add(cc[CC(0, 2, k)], cc[CC(0, 6, k)]), a6 = Sub(cc[CC(0, 2, k)], cc[CC(0, 6, k)]);
                Complex s = Add(a0, a2), d = Sub(a0, a2);
                ch[CH(0, k, 0)] = Add(s, a1);
                ch[CH(0, k, 4)] = Sub(s, a1);
                ch[CH(0, k, 2)] = Add(d, a3);
                ch[CH(0, k, 6)] = Sub(d, a3);
                a6 = RotX90(a6, fwd);
                s = Add(a4, a6);
                d = Sub(a4, a6);
                ch[CH(0, k, 1)] = Add(s, a5);
                ch[CH(0, k, 5)] = Sub(s, a5);
                ch[CH(0, k, 3)] = Add(d, a7);
                ch[CH(0, k, 7)] = Sub(d, a7);
            }
            for (int i = 1; i < ido; i++)
            {
                Complex a1 = Add(cc[CC(i, 1, k)], cc[CC(i, 5, k)]), a5 = Sub(cc[CC(i, 1, k)], cc[CC(i, 5, k)]);
                Complex a3 = Add(cc[CC(i, 3, k)], cc[CC(i, 7, k)]), a7 = Sub(cc[CC(i, 3, k)], cc[CC(i, 7, k)]);
                a7 = RotX90(a7, fwd);
                (a1, a3) = (Add(a1, a3), Sub(a1, a3));
                a3 = RotX90(a3, fwd);
                (a5, a7) = (Add(a5, a7), Sub(a5, a7));
                a5 = RotX45(a5, fwd);
                a7 = RotX135(a7, fwd);
                Complex a0 = Add(cc[CC(i, 0, k)], cc[CC(i, 4, k)]), a4 = Sub(cc[CC(i, 0, k)], cc[CC(i, 4, k)]);
                Complex a2 = Add(cc[CC(i, 2, k)], cc[CC(i, 6, k)]), a6 = Sub(cc[CC(i, 2, k)], cc[CC(i, 6, k)]);
                (a0, a2) = (Add(a0, a2), Sub(a0, a2));
                ch[CH(i, k, 0)] = Add(a0, a1);
                ch[CH(i, k, 4)] = SpecialMul(Sub(a0, a1), WA(3, i), fwd);
                ch[CH(i, k, 2)] = SpecialMul(Add(a2, a3), WA(1, i), fwd);
                ch[CH(i, k, 6)] = SpecialMul(Sub(a2, a3), WA(5, i), fwd);
                a6 = RotX90(a6, fwd);
                (a4, a6) = (Add(a4, a6), Sub(a4, a6));
                ch[CH(i, k, 1)] = SpecialMul(Add(a4, a5), WA(0, i), fwd);
                ch[CH(i, k, 5)] = SpecialMul(Sub(a4, a5), WA(4, i), fwd);
                ch[CH(i, k, 3)] = SpecialMul(Add(a6, a7), WA(2, i), fwd);
                ch[CH(i, k, 7)] = SpecialMul(Sub(a6, a7), WA(6, i), fwd);
            }
        }
    }

    /// <summary>pocketfft's sincos_2pibyn: two tables whose products give e^(2 pi i k / n).</summary>
    sealed class SinCos2PiByN
    {
        readonly int n, mask, shift;
        readonly Complex[] v1, v2;

        public SinCos2PiByN(int n)
        {
            this.n = n;
            // 0.25L*pi/n in long double, then to double: for n a power of two that is Math.PI / (4 n) exactly.
            double ang = Math.PI / 4 / n;
            int nval = (n + 2) / 2;
            shift = 1;
            while ((1 << shift) * (1 << shift) < nval)
                shift++;
            mask = (1 << shift) - 1;
            v1 = new Complex[mask + 1];
            v1[0] = new Complex(1, 0);
            for (int i = 1; i < v1.Length; i++)
                v1[i] = Calc(i, n, ang);
            v2 = new Complex[(nval + mask) / (mask + 1)];
            v2[0] = new Complex(1, 0);
            for (int i = 1; i < v2.Length; i++)
                v2[i] = Calc(i * (mask + 1), n, ang);
        }

        static Complex Calc(long x, long n, double ang)
        {
            x <<= 3;
            if (x < 4 * n)
            {
                if (x < 2 * n)
                {
                    if (x < n) return new(Libm.cos(x * ang), Libm.sin(x * ang));
                    return new(Libm.sin((2 * n - x) * ang), Libm.cos((2 * n - x) * ang));
                }
                x -= 2 * n;
                if (x < n) return new(-Libm.sin(x * ang), Libm.cos(x * ang));
                return new(-Libm.cos((2 * n - x) * ang), Libm.sin((2 * n - x) * ang));
            }
            x = 8 * n - x;
            if (x < 2 * n)
            {
                if (x < n) return new(Libm.cos(x * ang), -Libm.sin(x * ang));
                return new(Libm.sin((2 * n - x) * ang), -Libm.cos((2 * n - x) * ang));
            }
            x -= 2 * n;
            if (x < n) return new(-Libm.sin(x * ang), -Libm.cos(x * ang));
            return new(-Libm.cos((2 * n - x) * ang), -Libm.sin((2 * n - x) * ang));
        }

        public Complex this[int idx]
        {
            get
            {
                if (2 * idx <= n)
                {
                    Complex a = v1[idx & mask], b = v2[idx >> shift];
                    return new(a.Real * b.Real - a.Imaginary * b.Imaginary, a.Real * b.Imaginary + a.Imaginary * b.Real);
                }
                idx = n - idx;
                Complex c = v1[idx & mask], e = v2[idx >> shift];
                return new(c.Real * e.Real - c.Imaginary * e.Imaginary, -(c.Real * e.Imaginary + c.Imaginary * e.Real));
            }
        }
    }

    /// <summary>numpy.fft.fft2 / ifft2 of a square row-major array: rows first, then columns.</summary>
    public static Complex[] Fft2(Complex[] a, int size, bool forward)
    {
        var fft = new NumPyFft(size);
        double fct = forward ? 1.0 : 1.0 / size;
        var output = (Complex[])a.Clone();
        var line = new Complex[size];
        for (int y = 0; y < size; y++)
        {
            Array.Copy(output, y * size, line, 0, size);
            fft.Exec(line, fct, forward);
            Array.Copy(line, 0, output, y * size, size);
        }
        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size; y++)
                line[y] = output[y * size + x];
            fft.Exec(line, fct, forward);
            for (int y = 0; y < size; y++)
                output[y * size + x] = line[y];
        }
        return output;
    }

    /// <summary>numpy.fft.fftfreq(n) * n: 0, 1, ..., n/2 - 1, -n/2, ..., -1.</summary>
    public static double[] FftFreqTimesN(int n)
    {
        var k = new double[n];
        for (int i = 0; i < n; i++)
            k[i] = i < (n + 1) / 2 ? i : i - n;
        return k;
    }
}

public static class NumPyStats
{
    /// <summary>np.percentile(values, q), method 'linear', over all values.</summary>
    public static double Percentile(double[] values, double q)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        double quantile = q / 100;
        double virtualIndex = (sorted.Length - 1) * quantile;
        if (virtualIndex >= sorted.Length - 1)
            return sorted[^1];
        if (virtualIndex < 0)
            return sorted[0];
        double previous = Math.Floor(virtualIndex);
        double gamma = virtualIndex - previous;
        double a = sorted[(int)previous], b = sorted[(int)previous + 1];
        double diff = b - a;
        return gamma >= 0.5 ? b - diff * (1 - gamma) : a + diff * gamma;
    }
}
