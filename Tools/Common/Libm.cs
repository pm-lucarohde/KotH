using System.Runtime.InteropServices;

namespace KotH.Tools;

/// <summary>
/// The C library's maths, which Python, numpy and Pillow call: the tools reproduce their
/// results bit for bit, so they take the same functions rather than .NET's own.
/// </summary>
public static class Libm
{
    const string Lib = "libm.so.6";
    [DllImport(Lib)] public static extern double sin(double x);
    [DllImport(Lib)] public static extern double cos(double x);
    [DllImport(Lib)] public static extern double tan(double x);
    [DllImport(Lib)] public static extern double atan(double x);
    [DllImport(Lib)] public static extern double atan2(double y, double x);
    [DllImport(Lib)] public static extern double acos(double x);
    [DllImport(Lib)] public static extern double asin(double x);
    [DllImport(Lib)] public static extern double exp(double x);
    [DllImport(Lib)] public static extern double log(double x);
    [DllImport(Lib)] public static extern double log1p(double x);
    [DllImport(Lib)] public static extern double log10(double x);
    [DllImport(Lib)] public static extern double pow(double x, double y);
    [DllImport(Lib)] public static extern double hypot(double x, double y);
    [DllImport(Lib)] public static extern double fmod(double x, double y);
    [DllImport(Lib)] public static extern float sinf(float x);
    [DllImport(Lib)] public static extern float cosf(float x);
    [DllImport(Lib)] public static extern float powf(float x, float y);
    [DllImport(Lib)] public static extern float expf(float x);
}
