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
/// numpy.fft: pocketfft's cfftp (numpy 2.5.4), pass for pass, so the sums round as there.
/// Factors 2, 3, 4, 5, 8 and the generic pass for primes above 11; lengths that would need
/// pass7, pass11 or Bluestein are not needed and refused.
/// </summary>
public sealed class NumPyFft
{
    readonly int length;
    readonly List<(int Factor, Complex[] Twiddle, Complex[] Generic)> passes = [];

    public NumPyFft(int length)
    {
        if (length < 1)
            throw new NotSupportedException($"FFT der Länge {length}");
        this.length = length;
        if (length == 1)
            return;
        // pocketfft_c: Bluestein only for a large prime factor; then refused.
        long lpf = length < 50 ? 0 : LargestPrimeFactor(length);
        if (lpf * lpf > length && 1.5 * 2 * CostGuess(GoodSizeComplex(2 * length - 1)) < CostGuess(length))
            throw new NotSupportedException($"FFT der Länge {length}: Bluestein fehlt");
        // cfftp::factorize: eights, then fours, a two goes to the front, then odd divisors.
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
        for (int divisor = 3; divisor * divisor <= rest; divisor += 2)
            while (rest % divisor == 0)
            {
                factors.Add(divisor);
                rest /= divisor;
            }
        if (rest > 1)
            factors.Add(rest);
        if (factors.Contains(7) || factors.Contains(11))
            throw new NotSupportedException($"FFT der Länge {length}: Faktor 7 oder 11 fehlt");
        var twiddle = new SinCos2PiByN(length);
        int l1 = 1;
        foreach (int ip in factors)
        {
            int ido = length / (l1 * ip);
            var tw = new Complex[(ip - 1) * (ido - 1)];
            for (int j = 1; j < ip; j++)
                for (int i = 1; i < ido; i++)
                    tw[(j - 1) * (ido - 1) + i - 1] = twiddle[j * l1 * i];
            Complex[] tws = null;
            if (ip > 11)
            {
                tws = new Complex[ip];
                for (int j = 0; j < ip; j++)
                    tws[j] = twiddle[j * l1 * ido];
            }
            passes.Add((ip, tw, tws));
            l1 *= ip;
        }
    }

    static long LargestPrimeFactor(long n)
    {
        long res = 1;
        while ((n & 1) == 0) { res = 2; n >>= 1; }
        for (long x = 3; x * x <= n; x += 2)
            while (n % x == 0) { res = x; n /= x; }
        return n > 1 ? n : res;
    }

    static double CostGuess(long n)
    {
        const double lfp = 1.1;
        long ni = n;
        double result = 0;
        while ((n & 1) == 0) { result += 2; n >>= 1; }
        for (long x = 3; x * x <= n; x += 2)
            while (n % x == 0)
            {
                result += x <= 5 ? x : lfp * x;
                n /= x;
            }
        if (n > 1)
            result += n <= 5 ? n : lfp * n;
        return result * ni;
    }

    static long GoodSizeComplex(long n)
    {
        if (n <= 12)
            return n;
        long best = 2 * n;
        for (long f11 = 1; f11 < best; f11 *= 11)
            for (long f117 = f11; f117 < best; f117 *= 7)
                for (long f1175 = f117; f1175 < best; f1175 *= 5)
                {
                    long x = f1175;
                    while (x < n) x *= 2;
                    for (;;)
                    {
                        if (x < n) x *= 3;
                        else if (x > n)
                        {
                            if (x < best) best = x;
                            if ((x & 1) != 0) break;
                            x >>= 1;
                        }
                        else return n;
                    }
                }
        return best;
    }

    /// <summary>In place; fct scales the result (1/n for numpy's inverse).</summary>
    public void Exec(Complex[] c, double fct, bool forward)
    {
        if (length == 1)
        {
            c[0] = new Complex(c[0].Real * fct, c[0].Imaginary * fct);
            return;
        }
        var ch = new Complex[length];
        Complex[] p1 = c, p2 = ch;
        int l1 = 1;
        foreach (var (ip, tw, tws) in passes)
        {
            int ido = length / (ip * l1);
            if (ip == 4) Pass4(ido, l1, p1, p2, tw, forward);
            else if (ip == 8) Pass8(ido, l1, p1, p2, tw, forward);
            else if (ip == 2) Pass2(ido, l1, p1, p2, tw, forward);
            else if (ip == 3) Pass3(ido, l1, p1, p2, tw, forward);
            else if (ip == 5) Pass5(ido, l1, p1, p2, tw, forward);
            else
            {
                PassG(ido, ip, l1, p1, p2, tw, tws, forward);
                (p1, p2) = (p2, p1);
            }
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
    static Complex Scale(Complex a, double s) => new(a.Real * s, a.Imaginary * s);

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

    static void Pass3(int ido, int l1, Complex[] cc, Complex[] ch, Complex[] wa, bool fwd)
    {
        const double tw1r = -0.5;
        double tw1i = (fwd ? -1 : 1) * 0.8660254037844386467637231707529362;
        int CH(int a, int b, int c) => a + ido * (b + l1 * c);
        int CC(int a, int b, int c) => a + ido * (b + 3 * c);
        Complex WA(int x, int i) => wa[i - 1 + x * (ido - 1)];
        for (int k = 0; k < l1; k++)
            for (int i = 0; i < ido; i++)
            {
                Complex t0 = cc[CC(i, 0, k)];
                Complex t1 = Add(cc[CC(i, 1, k)], cc[CC(i, 2, k)]), t2 = Sub(cc[CC(i, 1, k)], cc[CC(i, 2, k)]);
                ch[CH(i, k, 0)] = Add(t0, t1);
                Complex ca = Add(t0, Scale(t1, tw1r));
                Complex cb = new(-t2.Imaginary * tw1i, t2.Real * tw1i);
                if (i == 0)
                {
                    ch[CH(0, k, 1)] = Add(ca, cb);
                    ch[CH(0, k, 2)] = Sub(ca, cb);
                }
                else
                {
                    ch[CH(i, k, 1)] = SpecialMul(Add(ca, cb), WA(0, i), fwd);
                    ch[CH(i, k, 2)] = SpecialMul(Sub(ca, cb), WA(1, i), fwd);
                }
            }
    }

    static void Pass4(int ido, int l1, Complex[] cc, Complex[] ch, Complex[] wa, bool fwd)
    {
        int CH(int a, int b, int c) => a + ido * (b + l1 * c);
        int CC(int a, int b, int c) => a + ido * (b + 4 * c);
        Complex WA(int x, int i) => wa[i - 1 + x * (ido - 1)];
        for (int k = 0; k < l1; k++)
            for (int i = 0; i < ido; i++)
            {
                Complex t2 = Add(cc[CC(i, 0, k)], cc[CC(i, 2, k)]), t1 = Sub(cc[CC(i, 0, k)], cc[CC(i, 2, k)]);
                Complex t3 = Add(cc[CC(i, 1, k)], cc[CC(i, 3, k)]), t4 = Sub(cc[CC(i, 1, k)], cc[CC(i, 3, k)]);
                t4 = RotX90(t4, fwd);
                if (i == 0)
                {
                    ch[CH(0, k, 0)] = Add(t2, t3);
                    ch[CH(0, k, 2)] = Sub(t2, t3);
                    ch[CH(0, k, 1)] = Add(t1, t4);
                    ch[CH(0, k, 3)] = Sub(t1, t4);
                }
                else
                {
                    ch[CH(i, k, 0)] = Add(t2, t3);
                    ch[CH(i, k, 1)] = SpecialMul(Add(t1, t4), WA(0, i), fwd);
                    ch[CH(i, k, 2)] = SpecialMul(Sub(t2, t3), WA(1, i), fwd);
                    ch[CH(i, k, 3)] = SpecialMul(Sub(t1, t4), WA(2, i), fwd);
                }
            }
    }

    static void Pass5(int ido, int l1, Complex[] cc, Complex[] ch, Complex[] wa, bool fwd)
    {
        const double tw1r = 0.3090169943749474241022934171828191, tw2r = -0.8090169943749474241022934171828191;
        double tw1i = (fwd ? -1 : 1) * 0.9510565162951535721164393333793821;
        double tw2i = (fwd ? -1 : 1) * 0.5877852522924731291687059546390728;
        int CH(int a, int b, int c) => a + ido * (b + l1 * c);
        int CC(int a, int b, int c) => a + ido * (b + 5 * c);
        Complex WA(int x, int i) => wa[i - 1 + x * (ido - 1)];
        for (int k = 0; k < l1; k++)
            for (int i = 0; i < ido; i++)
            {
                Complex t0 = cc[CC(i, 0, k)];
                Complex t1 = Add(cc[CC(i, 1, k)], cc[CC(i, 4, k)]), t4 = Sub(cc[CC(i, 1, k)], cc[CC(i, 4, k)]);
                Complex t2 = Add(cc[CC(i, 2, k)], cc[CC(i, 3, k)]), t3 = Sub(cc[CC(i, 2, k)], cc[CC(i, 3, k)]);
                ch[CH(i, k, 0)] = new(t0.Real + t1.Real + t2.Real, t0.Imaginary + t1.Imaginary + t2.Imaginary);
                // The macro's sign tokens: (1,4) adds both sine terms, (2,3) subtracts the second.
                Complex ca = new(t0.Real + tw1r * t1.Real + tw2r * t2.Real, t0.Imaginary + tw1r * t1.Imaginary + tw2r * t2.Imaginary);
                Complex cb = new(-(tw1i * t4.Imaginary + tw2i * t3.Imaginary), tw1i * t4.Real + tw2i * t3.Real);
                Step(i, k, 1, 4, ca, cb);
                ca = new(t0.Real + tw2r * t1.Real + tw1r * t2.Real, t0.Imaginary + tw2r * t1.Imaginary + tw1r * t2.Imaginary);
                cb = new(-(tw2i * t4.Imaginary - tw1i * t3.Imaginary), tw2i * t4.Real - tw1i * t3.Real);
                Step(i, k, 2, 3, ca, cb);
            }

        void Step(int i, int k, int u1, int u2, Complex ca, Complex cb)
        {
            if (i == 0)
            {
                ch[CH(0, k, u1)] = Add(ca, cb);
                ch[CH(0, k, u2)] = Sub(ca, cb);
            }
            else
            {
                ch[CH(i, k, u1)] = SpecialMul(Add(ca, cb), WA(u1 - 1, i), fwd);
                ch[CH(i, k, u2)] = SpecialMul(Sub(ca, cb), WA(u2 - 1, i), fwd);
            }
        }
    }

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

    /// <summary>pocketfft's passg for a prime factor above 11; leaves its result in cc.</summary>
    static void PassG(int ido, int ip, int l1, Complex[] cc, Complex[] ch, Complex[] wa, Complex[] csarr, bool fwd)
    {
        int cdim = ip, ipph = (ip + 1) / 2, idl1 = ido * l1;
        int CH(int a, int b, int c) => a + ido * (b + l1 * c);
        int CC(int a, int b, int c) => a + ido * (b + cdim * c);
        int CX(int a, int b, int c) => a + ido * (b + l1 * c);
        int X2(int a, int b) => a + idl1 * b;

        var wal = new Complex[ip];
        wal[0] = new Complex(1, 0);
        for (int i = 1; i < ip; i++)
            wal[i] = new Complex(csarr[i].Real, fwd ? -csarr[i].Imaginary : csarr[i].Imaginary);

        for (int k = 0; k < l1; k++)
            for (int i = 0; i < ido; i++)
                ch[CH(i, k, 0)] = cc[CC(i, 0, k)];
        for (int j = 1, jc = ip - 1; j < ipph; j++, jc--)
            for (int k = 0; k < l1; k++)
                for (int i = 0; i < ido; i++)
                {
                    Complex a = cc[CC(i, j, k)], b = cc[CC(i, jc, k)];
                    ch[CH(i, k, j)] = Add(a, b);
                    ch[CH(i, k, jc)] = Sub(a, b);
                }
        for (int k = 0; k < l1; k++)
            for (int i = 0; i < ido; i++)
            {
                Complex tmp = ch[CH(i, k, 0)];
                for (int j = 1; j < ipph; j++)
                    tmp = Add(tmp, ch[CH(i, k, j)]);
                cc[CX(i, k, 0)] = tmp;
            }
        for (int l = 1, lc = ip - 1; l < ipph; l++, lc--)
        {
            for (int ik = 0; ik < idl1; ik++)
            {
                cc[X2(ik, l)] = new(
                    ch[X2(ik, 0)].Real + wal[l].Real * ch[X2(ik, 1)].Real + wal[2 * l].Real * ch[X2(ik, 2)].Real,
                    ch[X2(ik, 0)].Imaginary + wal[l].Real * ch[X2(ik, 1)].Imaginary + wal[2 * l].Real * ch[X2(ik, 2)].Imaginary);
                cc[X2(ik, lc)] = new(
                    -wal[l].Imaginary * ch[X2(ik, ip - 1)].Imaginary - wal[2 * l].Imaginary * ch[X2(ik, ip - 2)].Imaginary,
                    wal[l].Imaginary * ch[X2(ik, ip - 1)].Real + wal[2 * l].Imaginary * ch[X2(ik, ip - 2)].Real);
            }
            int iwal = 2 * l;
            int j = 3, jc = ip - 3;
            for (; j < ipph - 1; j += 2, jc -= 2)
            {
                iwal += l; if (iwal > ip) iwal -= ip;
                Complex xwal = wal[iwal];
                iwal += l; if (iwal > ip) iwal -= ip;
                Complex xwal2 = wal[iwal];
                for (int ik = 0; ik < idl1; ik++)
                {
                    Complex p = cc[X2(ik, l)], q = cc[X2(ik, lc)];
                    cc[X2(ik, l)] = new(
                        p.Real + (ch[X2(ik, j)].Real * xwal.Real + ch[X2(ik, j + 1)].Real * xwal2.Real),
                        p.Imaginary + (ch[X2(ik, j)].Imaginary * xwal.Real + ch[X2(ik, j + 1)].Imaginary * xwal2.Real));
                    cc[X2(ik, lc)] = new(
                        q.Real - (ch[X2(ik, jc)].Imaginary * xwal.Imaginary + ch[X2(ik, jc - 1)].Imaginary * xwal2.Imaginary),
                        q.Imaginary + (ch[X2(ik, jc)].Real * xwal.Imaginary + ch[X2(ik, jc - 1)].Real * xwal2.Imaginary));
                }
            }
            for (; j < ipph; j++, jc--)
            {
                iwal += l; if (iwal > ip) iwal -= ip;
                Complex xwal = wal[iwal];
                for (int ik = 0; ik < idl1; ik++)
                {
                    Complex p = cc[X2(ik, l)], q = cc[X2(ik, lc)];
                    cc[X2(ik, l)] = new(p.Real + ch[X2(ik, j)].Real * xwal.Real, p.Imaginary + ch[X2(ik, j)].Imaginary * xwal.Real);
                    cc[X2(ik, lc)] = new(q.Real - ch[X2(ik, jc)].Imaginary * xwal.Imaginary, q.Imaginary + ch[X2(ik, jc)].Real * xwal.Imaginary);
                }
            }
        }

        // Shuffling and twiddling.
        if (ido == 1)
            for (int j = 1, jc = ip - 1; j < ipph; j++, jc--)
                for (int ik = 0; ik < idl1; ik++)
                {
                    Complex t1 = cc[X2(ik, j)], t2 = cc[X2(ik, jc)];
                    cc[X2(ik, j)] = Add(t1, t2);
                    cc[X2(ik, jc)] = Sub(t1, t2);
                }
        else
            for (int j = 1, jc = ip - 1; j < ipph; j++, jc--)
                for (int k = 0; k < l1; k++)
                {
                    Complex t1 = cc[CX(0, k, j)], t2 = cc[CX(0, k, jc)];
                    cc[CX(0, k, j)] = Add(t1, t2);
                    cc[CX(0, k, jc)] = Sub(t1, t2);
                    for (int i = 1; i < ido; i++)
                    {
                        Complex x1 = Add(cc[CX(i, k, j)], cc[CX(i, k, jc)]), x2 = Sub(cc[CX(i, k, j)], cc[CX(i, k, jc)]);
                        cc[CX(i, k, j)] = SpecialMul(x1, wa[(j - 1) * (ido - 1) + i - 1], fwd);
                        cc[CX(i, k, jc)] = SpecialMul(x2, wa[(jc - 1) * (ido - 1) + i - 1], fwd);
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
            double ang = QuarterPiOver(n);
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

        // 0.25L*pi/n: pi and the quotient in x87 long double (64-bit mantissa, round to even),
        // then rounded once more to double.
        static double QuarterPiOver(long n)
        {
            const ulong piMantissa = 0xC90FDAA22168C235; // pi = piMantissa * 2^-62
            BigInteger num = (BigInteger)piMantissa << 128;
            BigInteger q = BigInteger.DivRem(num, n, out BigInteger rem);
            int drop = (int)q.GetBitLength() - 64;
            q = RoundEven(q, drop, !rem.IsZero);
            BigInteger m53 = RoundEven(q, 11, false);
            // q * 2^drop * 2^-(64+128) is the long double; m53 * 2^11 the same scale.
            return Math.ScaleB((double)m53, drop + 11 - 64 - 128);
        }

        static BigInteger RoundEven(BigInteger x, int bits, bool sticky)
        {
            BigInteger q = x >> bits, r = x - (q << bits), half = BigInteger.One << (bits - 1);
            if (r > half || (r == half && (sticky || !q.IsEven)))
                q += 1;
            return q;
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
    public static Complex[] Fft2(Complex[] a, int size, bool forward) => Fft2(a, size, size, forward);

    /// <summary>numpy.fft.fft2 / ifft2 of a rows x cols row-major array: the last axis first.</summary>
    public static Complex[] Fft2(Complex[] a, int rows, int cols, bool forward)
    {
        var output = (Complex[])a.Clone();
        var fft = new NumPyFft(cols);
        double fct = forward ? 1.0 : 1.0 / cols;
        var line = new Complex[cols];
        for (int y = 0; y < rows; y++)
        {
            Array.Copy(output, y * cols, line, 0, cols);
            fft.Exec(line, fct, forward);
            Array.Copy(line, 0, output, y * cols, cols);
        }
        fft = new NumPyFft(rows);
        fct = forward ? 1.0 : 1.0 / rows;
        line = new Complex[rows];
        for (int x = 0; x < cols; x++)
        {
            for (int y = 0; y < rows; y++)
                line[y] = output[y * cols + x];
            fft.Exec(line, fct, forward);
            for (int y = 0; y < rows; y++)
                output[y * cols + x] = line[y];
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

/// <summary>
/// np.convolve(a, v) of float64 arrays as numpy 2.5.4 computes it on this machine: _pyarray_correlate
/// with the kernel reversed, every output a cblas_ddot of OpenBLAS 0.3.34 (DYNAMIC_ARCH, Zen/Haswell
/// kernel: 16 products at a time in four FMA registers, then the rest one by one, not fused). Kernels
/// of up to 11 elements take numpy's small_correlate instead, which is not needed and refused.
/// </summary>
public static class NumPyConvolve
{
    /// <summary>np.convolve(a, v, 'same').</summary>
    public static double[] Same(double[] a, double[] v)
    {
        if (v.Length > a.Length)
            (a, v) = (v, a);
        if (v.Length <= 11)
            throw new NotSupportedException("small_correlate fehlt");
        var w = (double[])v.Clone();
        Array.Reverse(w);
        int n1 = a.Length, n2 = w.Length;
        int nLeft = n2 / 2, nRight = n2 - nLeft - 1;
        var output = new double[n1];
        int o = 0, ip1 = 0, ip2 = nLeft, n = n2 - nLeft;
        for (int i = 0; i < nLeft; i++, n++, ip2--)
            output[o++] = 0.0 + Ddot(a, ip1, w, ip2, n);
        for (int i = 0; i < n1 - n2 + 1; i++, ip1++)
            output[o++] = 0.0 + Ddot(a, ip1, w, ip2, n);
        for (int i = 0; i < nRight; i++, ip1++)
        {
            n--;
            output[o++] = 0.0 + Ddot(a, ip1, w, ip2, n);
        }
        return output;
    }

    // dot_compute with ddot_microk_haswell-2.c, both strides 1.
    static double Ddot(double[] x, int xo, double[] y, int yo, int n)
    {
        double dot = 0.0;
        int n1 = n & -16;
        if (n1 > 0)
        {
            Span<double> acc = stackalloc double[16];
            acc.Clear();
            for (int i = 0; i < n1; i += 16)
                for (int l = 0; l < 16; l++)
                    acc[l] = Math.FusedMultiplyAdd(x[xo + i + l], y[yo + i + l], acc[l]);
            // Register r holds acc[4r..4r+3]; its halves are added, then the registers pairwise.
            double r4a = acc[0] + acc[2], r4b = acc[1] + acc[3], r5a = acc[4] + acc[6], r5b = acc[5] + acc[7];
            double r6a = acc[8] + acc[10], r6b = acc[9] + acc[11], r7a = acc[12] + acc[14], r7b = acc[13] + acc[15];
            double s4a = r4a + r5a, s4b = r4b + r5b, s6a = r6a + r7a, s6b = r6b + r7b;
            dot = (s4a + s6a) + (s4b + s6b);
        }
        for (int i = n1; i < n; i++)
            dot += y[yo + i] * x[xo + i];
        return dot;
    }
}

public static class NumPyStats
{
    /// <summary>np.sum of a contiguous float64 array: the identity 0 plus numpy's pairwise sum.</summary>
    public static double Sum(double[] a) => 0.0 + PairwiseSum(a, 0, a.Length);

    /// <summary>np.mean of a contiguous float64 array.</summary>
    public static double Mean(double[] a) => Sum(a) / a.Length;

    /// <summary>np.std of a float64 array (ddof 0): the mean, then the squared deviations summed pairwise.</summary>
    public static double Std(double[] a)
    {
        double mean = Mean(a);
        var sq = new double[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            double x = a[i] - mean;
            sq[i] = x * x;
        }
        return Math.Sqrt(Sum(sq) / a.Length);
    }

    /// <summary>np.mean of a contiguous float32 array: float32 pairwise sum; the division by the
    /// count (an intp) runs in float64 and is cast back.</summary>
    public static float Mean(float[] a) => (float)((double)(0.0f + PairwiseSum(a, 0, a.Length)) / a.Length);

    /// <summary>np.mean of the float32 slice a[y0:y1, x0:x1] of a row-major array: numpy's buffered
    /// reduction copies as many whole rows as fit in 8192 elements and sums each buffer pairwise
    /// (a slice of whole rows is contiguous and summed in one go). Bounds clamp like a slice.</summary>
    public static float MeanSlice(float[] a, int width, int y0, int y1, int x0, int x1)
    {
        int height = a.Length / width;
        y1 = Math.Min(y1, height);
        x1 = Math.Min(x1, width);
        int rw = x1 - x0, n = (y1 - y0) * rw;
        var flat = new float[n];
        for (int y = y0; y < y1; y++)
            Array.Copy(a, y * width + x0, flat, (y - y0) * rw, rw);
        float sum = 0.0f;
        if (rw == width)
            sum += PairwiseSum(flat, 0, n);
        else
        {
            int per = Math.Max(1, 8192 / rw) * rw;
            for (int s = 0; s < n; s += per)
                sum += PairwiseSum(flat, s, Math.Min(per, n - s));
        }
        return (float)((double)sum / n);
    }

    /// <summary>str() of a numpy float32: the shortest digits, positional from 1e-4 to 1e8.</summary>
    public static string Str(float v)
    {
        if (v == 0)
            return float.IsNegative(v) ? "-0.0" : "0.0";
        string r = Math.Abs(v).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        int e = r.IndexOf('E');
        int exponent = e < 0 ? 0 : int.Parse(r[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        string mantissa = e < 0 ? r : r[..e];
        int point = mantissa.IndexOf('.');
        string digits = mantissa.Replace(".", "");
        int decpt = (point < 0 ? mantissa.Length : point) + exponent;
        int lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        decpt -= lead;
        string sign = float.IsNegative(v) ? "-" : "";
        if (Math.Abs(v) < 1e-4f || Math.Abs(v) >= 1e8f)
        {
            string rest = digits.Length > 1 ? "." + digits[1..] : "";
            int x = decpt - 1;
            return sign + digits[0] + rest + (x < 0 ? "e-" : "e+") + Math.Abs(x).ToString("00");
        }
        if (decpt <= 0)
            return sign + "0." + new string('0', -decpt) + digits;
        if (decpt >= digits.Length)
            return sign + digits + new string('0', decpt - digits.Length) + ".0";
        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    /// <summary>np.percentile(a, q) of a float32 array with q a Python number: the linear
    /// interpolation runs in float32 with the weight as a weak Python float.</summary>
    public static float Percentile(float[] values, double q)
    {
        float[] s = [.. values];
        Array.Sort(s);
        int n = s.Length;
        double virtualIndex = (n - 1) * (q / 100);
        double previous = Math.Floor(virtualIndex);
        double gamma = virtualIndex - previous;
        int lo = (int)previous, hi = lo + 1;
        if (virtualIndex >= n - 1)
            lo = hi = n - 1;
        float a = s[lo], b = s[hi], diff = b - a;
        float result = a + diff * (float)gamma;
        if (gamma >= 0.5)
            result = b - diff * (float)(1 - gamma);
        return result;
    }

    // FLOAT_pairwise_sum: the same blocks, accumulated in float32.
    public static float PairwiseSum(float[] a, int start, int n)
    {
        if (n < 8)
        {
            float res = 0.0f;
            for (int i = 0; i < n; i++)
                res += a[start + i];
            return res;
        }
        if (n <= 128)
        {
            var r = new float[8];
            for (int j = 0; j < 8; j++)
                r[j] = a[start + j];
            int i;
            for (i = 8; i < n - n % 8; i += 8)
                for (int j = 0; j < 8; j++)
                    r[j] += a[start + i + j];
            float res = ((r[0] + r[1]) + (r[2] + r[3])) + ((r[4] + r[5]) + (r[6] + r[7]));
            for (; i < n; i++)
                res += a[start + i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSum(a, start, n2) + PairwiseSum(a, start + n2, n - n2);
    }

    // DOUBLE_pairwise_sum (loops_utils.h.src): blocks of 128 with eight accumulators.
    static double PairwiseSum(double[] a, int start, int n)
    {
        if (n < 8)
        {
            double res = 0.0;
            for (int i = 0; i < n; i++)
                res += a[start + i];
            return res;
        }
        if (n <= 128)
        {
            var r = new double[8];
            for (int j = 0; j < 8; j++)
                r[j] = a[start + j];
            int i;
            for (i = 8; i < n - n % 8; i += 8)
                for (int j = 0; j < 8; j++)
                    r[j] += a[start + i + j];
            double res = ((r[0] + r[1]) + (r[2] + r[3])) + ((r[4] + r[5]) + (r[6] + r[7]));
            for (; i < n; i++)
                res += a[start + i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSum(a, start, n2) + PairwiseSum(a, start + n2, n - n2);
    }

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
