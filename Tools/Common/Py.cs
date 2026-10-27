using System;
using System.Globalization;

namespace KotH.Tools;

/// <summary>Python's formatting where the tools print numbers as the old scripts did.</summary>
public static class Py
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Python's "%.{precision}g": shortest of fixed and exponent, trailing zeros dropped.</summary>
    public static string G(double v, int precision = 6)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        int p = Math.Max(precision, 1);
        if (v == 0) return double.IsNegative(v) ? "-0" : "0";
        // Rounded to p digits first, so the exponent is the printed one.
        string e = v.ToString("E" + (p - 1), Inv);
        int x = int.Parse(e[(e.IndexOf('E') + 1)..], Inv);
        if (x >= -4 && x < p)
            return Strip(v.ToString("F" + (p - 1 - x), Inv));
        string mantissa = Strip(e[..e.IndexOf('E')]);
        return mantissa + (x < 0 ? "e-" : "e+") + Math.Abs(x).ToString("00", Inv);
    }

    /// <summary>Python's "%.{decimals}f".</summary>
    public static string F(double v, int decimals) => v.ToString("F" + decimals, Inv);

    static string Strip(string s) => s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;

    /// <summary>Python's float() of a number in the scripts' text: invariant, with exponent.</summary>
    public static double Float(string s) => double.Parse(s, NumberStyles.Float, Inv);
}
