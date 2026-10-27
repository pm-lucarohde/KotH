using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace KotH.Tools;

/// <summary>
/// Python's arithmetic and formatting where it differs from C#'s, so a ported script
/// computes and prints the same: float sum() is compensated (CPython 3.12+), hypot has
/// its own algorithm, % and // floor, round() and "%f" round half to even on the exact value.
/// </summary>
public static class Py
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>sum() of floats from int 0: Neumaier's compensated sum, as CPython's builtin.</summary>
    public static double Sum(IEnumerable<double> values)
    {
        using var e = values.GetEnumerator();
        if (!e.MoveNext())
            return 0.0;
        double hi = 0.0 + e.Current, lo = 0.0;
        while (e.MoveNext())
        {
            double x = e.Current;
            double t = hi + x;
            if (Math.Abs(hi) >= Math.Abs(x))
                lo += (hi - t) + x;
            else
                lo += (x - t) + hi;
            hi = t;
        }
        return lo != 0 && double.IsFinite(lo) ? hi + lo : hi;
    }

    public static double Sum(double a, double b, double c) => Sum([a, b, c]);

    /// <summary>math.hypot / math.dist: CPython's vector_norm with lossless scaling and a correction step.</summary>
    public static double Hypot(params double[] values)
    {
        double max = 0.0;
        var vec = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            vec[i] = Math.Abs(values[i]);
            if (vec[i] > max)
                max = vec[i];
        }
        if (double.IsInfinity(max))
            return max;
        if (max == 0.0 || vec.Length <= 1)
            return max;
        int maxE = Math.ILogB(max) + 1;    // frexp's exponent
        if (maxE < -1023)
            throw new NotSupportedException("hypot subnormaler Werte");
        double scale = Math.ScaleB(1.0, -maxE);
        double csum = 1.0, frac1 = 0.0, frac2 = 0.0;
        foreach (double v in vec)
        {
            double x = v * scale;
            double prHi = x * x, prLo = Math.FusedMultiplyAdd(x, x, -prHi);
            double smHi = csum + prHi, smLo = (csum - smHi) + prHi;
            csum = smHi;
            frac1 += prLo;
            frac2 += smLo;
        }
        double h = Math.Sqrt(csum - 1.0 + (frac1 + frac2));
        {
            double prHi = -h * h, prLo = Math.FusedMultiplyAdd(-h, h, -prHi);
            double smHi = csum + prHi, smLo = (csum - smHi) + prHi;
            csum = smHi;
            frac1 += prLo;
            frac2 += smLo;
        }
        double xr = csum - 1.0 + (frac1 + frac2);
        h += xr / (2.0 * h);
        return h / scale;
    }

    /// <summary>Python's float %: the sign of the divisor.</summary>
    public static double Mod(double a, double b)
    {
        double mod = a % b;
        if (mod != 0)
        {
            if ((b < 0) != (mod < 0))
                mod += b;
        }
        else
            mod = b < 0 ? -0.0 : 0.0;
        return mod;
    }

    public static int Mod(int a, int b) => ((a % b) + b) % b;
    public static long Mod(long a, long b) => ((a % b) + b) % b;
    /// <summary>Python's int //: rounded towards minus infinity.</summary>
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return (a % b != 0) && ((a < 0) != (b < 0)) ? q - 1 : q;
    }

    /// <summary>round(x): to the nearest int, halves to even.</summary>
    public static int Round(double x) => (int)Math.Round(x, MidpointRounding.ToEven);

    /// <summary>float ** float: the C library's pow, as CPython's float_pow (no special case changes a finite result here).</summary>
    public static double Pow(double x, double y)
    {
        if (y == 0.0)
            return 1.0;
        if (x == 1.0)
            return 1.0;
        return Libm.pow(x, y);
    }

    /// <summary>max(a, b) and min(a, b): the first wins a tie, as in Python.</summary>
    public static double Max(double a, double b) => b > a ? b : a;
    public static double Min(double a, double b) => b < a ? b : a;

    /// <summary>Python's "%.{precision}g".</summary>
    public static string G(double v, int precision = 6)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        int p = Math.Max(precision, 1);
        string sign = double.IsNegative(v) ? "-" : "";
        if (v == 0)
            return sign + "0";
        var (num, den) = Exact(Math.Abs(v));
        // The decimal exponent of the value, then of the value rounded to p digits.
        int e10 = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        while (Compare(num, den, e10) < 0) e10--;
        while (Compare(num, den, e10 + 1) >= 0) e10++;
        BigInteger q = RoundScaled(num, den, p - 1 - e10);
        if (q == BigInteger.Pow(10, p))
        {
            e10++;
            q /= 10;
        }
        string digits = q.ToString(Inv);
        if (e10 >= -4 && e10 < p)
            return sign + Strip(Place(digits, p - 1 - e10));
        string mantissa = Strip(Place(digits, p - 1));
        return sign + mantissa + (e10 < 0 ? "e-" : "e+") + Math.Abs(e10).ToString("00", Inv);
    }

    /// <summary>Python's "%.{decimals}f".</summary>
    public static string F(double v, int decimals)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        var (num, den) = Exact(Math.Abs(v));
        string digits = RoundScaled(num, den, decimals).ToString(Inv);
        return (double.IsNegative(v) ? "-" : "") + Place(digits, decimals);
    }

    /// <summary>Python's float() of a number in the scripts' text: invariant, with exponent.</summary>
    public static double Float(string s) => double.Parse(s, NumberStyles.Float, Inv);

    /// <summary>repr(float): the shortest digits that read back, fixed from 1e-4 to 1e16, else with an exponent.</summary>
    public static string Repr(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        string sign = double.IsNegative(v) ? "-" : "";
        if (v == 0)
            return sign + "0.0";
        // .NET's "R" is the shortest round trip too; only the layout differs.
        string r = Math.Abs(v).ToString("R", Inv);
        int e = r.IndexOf('E');
        int exponent = e < 0 ? 0 : int.Parse(r[(e + 1)..], Inv);
        string mantissa = e < 0 ? r : r[..e];
        int point = mantissa.IndexOf('.');
        string digits = mantissa.Replace(".", "");
        int decpt = (point < 0 ? mantissa.Length : point) + exponent;
        int lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        decpt -= lead;
        digits = digits.TrimEnd('0');
        if (decpt <= -4 || decpt > 16)
        {
            string rest = digits.Length > 1 ? "." + digits[1..] : "";
            int x = decpt - 1;
            return sign + digits[0] + rest + (x < 0 ? "e-" : "e+") + Math.Abs(x).ToString("00", Inv);
        }
        if (decpt <= 0)
            return sign + "0." + new string('0', -decpt) + digits;
        if (decpt >= digits.Length)
            return sign + digits + new string('0', decpt - digits.Length) + ".0";
        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    // The exact value of a finite non-negative double as num / den.
    static (BigInteger, BigInteger) Exact(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent = 1;
        else
            mantissa |= 1L << 52;
        int e = exponent - 1075;
        return e >= 0 ? (new BigInteger(mantissa) << e, BigInteger.One) : (new BigInteger(mantissa), BigInteger.One << -e);
    }

    // num/den against 10^e10.
    static int Compare(BigInteger num, BigInteger den, int e10) => e10 >= 0
        ? num.CompareTo(den * BigInteger.Pow(10, e10))
        : (num * BigInteger.Pow(10, -e10)).CompareTo(den);

    // num/den * 10^shift to the nearest integer, halves to even.
    static BigInteger RoundScaled(BigInteger num, BigInteger den, int shift)
    {
        if (shift >= 0)
            num *= BigInteger.Pow(10, shift);
        else
            den *= BigInteger.Pow(10, -shift);
        BigInteger q = BigInteger.DivRem(num, den, out BigInteger r);
        int c = (r * 2).CompareTo(den);
        if (c > 0 || (c == 0 && !q.IsEven))
            q++;
        return q;
    }

    // The digits with a point before the last 'decimals' of them.
    static string Place(string digits, int decimals)
    {
        if (decimals <= 0)
            return digits + new string('0', -decimals);
        digits = digits.PadLeft(decimals + 1, '0');
        return digits[..^decimals] + "." + digits[^decimals..];
    }

    static string Strip(string s) => s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;
}

/// <summary>
/// Python's random.Random: MT19937 seeded by init_by_array from the int's 32-bit words,
/// with random(), getrandbits(), and the methods built on them as in random.py (3.14).
/// </summary>
public sealed class PyRandom
{
    readonly uint[] mt = new uint[624];
    int index;
    double? gaussNext;

    public PyRandom(long seed)
    {
        ulong n = (ulong)Math.Abs(seed);
        var key = new List<uint>();
        do
        {
            key.Add((uint)n);
            n >>= 32;
        } while (n != 0);
        InitByArray(key.ToArray());
    }

    void InitGenrand(uint s)
    {
        mt[0] = s;
        for (int i = 1; i < 624; i++)
            mt[i] = 1812433253u * (mt[i - 1] ^ (mt[i - 1] >> 30)) + (uint)i;
        index = 624;
    }

    void InitByArray(uint[] key)
    {
        InitGenrand(19650218u);
        int i = 1, j = 0;
        for (int k = Math.Max(624, key.Length); k > 0; k--)
        {
            mt[i] = (mt[i] ^ ((mt[i - 1] ^ (mt[i - 1] >> 30)) * 1664525u)) + key[j] + (uint)j;
            i++;
            j++;
            if (i >= 624) { mt[0] = mt[623]; i = 1; }
            if (j >= key.Length) j = 0;
        }
        for (int k = 623; k > 0; k--)
        {
            mt[i] = (mt[i] ^ ((mt[i - 1] ^ (mt[i - 1] >> 30)) * 1566083941u)) - (uint)i;
            i++;
            if (i >= 624) { mt[0] = mt[623]; i = 1; }
        }
        mt[0] = 0x80000000u;
    }

    public uint NextUInt32()
    {
        if (index >= 624)
        {
            for (int k = 0; k < 624; k++)
            {
                uint y = (mt[k] & 0x80000000u) | (mt[(k + 1) % 624] & 0x7fffffffu);
                mt[k] = mt[(k + 397) % 624] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908b0dfu : 0u);
            }
            index = 0;
        }
        uint z = mt[index++];
        z ^= z >> 11;
        z ^= (z << 7) & 0x9d2c5680u;
        z ^= (z << 15) & 0xefc60000u;
        z ^= z >> 18;
        return z;
    }

    public double Random()
    {
        uint a = NextUInt32() >> 5, b = NextUInt32() >> 6;
        return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
    }

    /// <summary>getrandbits(k) for k up to 64.</summary>
    public ulong GetRandBits(int k)
    {
        if (k <= 32)
            return NextUInt32() >> (32 - k);
        // Words from the least significant up, the last one cut to the bits left.
        ulong result = 0;
        int shift = 0;
        for (int left = k; left > 0; left -= 32, shift += 32)
        {
            uint word = NextUInt32();
            if (left < 32)
                word >>= 32 - left;
            result |= (ulong)word << shift;
        }
        return result;
    }

    long RandBelow(long n)
    {
        int k = 64 - BitOperations.LeadingZeroCount((ulong)n);
        long r = (long)GetRandBits(k);
        while (r >= n)
            r = (long)GetRandBits(k);
        return r;
    }

    public long RandRange(long stop) => stop > 0 ? RandBelow(stop) : throw new ArgumentException("empty range for randrange()");
    public long RandRange(long start, long stop) => start + RandBelow(stop - start);
    public int RandInt(int a, int b) => a + (int)RandBelow((long)b - a + 1);
    public T Choice<T>(IReadOnlyList<T> seq) => seq[(int)RandBelow(seq.Count)];
    public double Uniform(double a, double b) => a + (b - a) * Random();

    public double Gauss(double mu = 0.0, double sigma = 1.0)
    {
        double? z = gaussNext;
        gaussNext = null;
        if (z == null)
        {
            double x2pi = Random() * (2.0 * Math.PI);
            double g2rad = Math.Sqrt(-2.0 * Libm.log(1.0 - Random()));
            z = Libm.cos(x2pi) * g2rad;
            gaussNext = Libm.sin(x2pi) * g2rad;
        }
        return mu + z.Value * sigma;
    }
}
