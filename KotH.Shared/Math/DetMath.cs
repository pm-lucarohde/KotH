using System;

namespace KotH.Shared.Mathematics
{
    /// <summary>Sine, cosine, arc tangent and arc cosine with the same bits on every system:
    /// Math.Sin and friends call the C runtime (glibc, UCRT), which may differ in the last bit and
    /// split Linux from Windows peers. musl's (fdlibm's) double routines ported line by line, IEEE
    /// arithmetic and Math.Sqrt only; error below one ulp.</summary>
    public static class DetMath
    {
        public static double Sin(double x)
        {
            uint ix = High(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb)
                return ix < 0x3e500000 ? x : KernelSin(x, 0.0, false);
            if (ix >= 0x7ff00000)
                return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            return (n & 3) switch
            {
                0 => KernelSin(y0, y1, true),
                1 => KernelCos(y0, y1),
                2 => -KernelSin(y0, y1, true),
                _ => -KernelCos(y0, y1),
            };
        }

        public static double Cos(double x)
        {
            uint ix = High(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb)
                return ix < 0x3e46a09e ? 1.0 : KernelCos(x, 0.0);
            if (ix >= 0x7ff00000)
                return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            return (n & 3) switch
            {
                0 => KernelCos(y0, y1),
                1 => -KernelSin(y0, y1, true),
                2 => -KernelCos(y0, y1),
                _ => KernelSin(y0, y1, true),
            };
        }

        /// <summary>Float sine through the double one, rounded once.</summary>
        public static float SinF(float x) => (float)Sin(x);
        public static float CosF(float x) => (float)Cos(x);

        public static double Atan(double x)
        {
            uint hx = High(x);
            bool negative = hx >> 31 != 0;
            uint ix = hx & 0x7fffffff;
            int id;
            if (ix >= 0x44100000)
            {
                // |x| >= 2^66
                if (double.IsNaN(x))
                    return x;
                return negative ? -AtanHi[3] : AtanHi[3];
            }
            if (ix < 0x3fdc0000)
            {
                // |x| < 0.4375
                if (ix < 0x3e400000)
                    return x;
                id = -1;
            }
            else
            {
                x = System.Math.Abs(x);
                if (ix < 0x3ff30000)
                {
                    if (ix < 0x3fe60000)
                    {
                        id = 0;
                        x = (2.0 * x - 1.0) / (2.0 + x);
                    }
                    else
                    {
                        id = 1;
                        x = (x - 1.0) / (x + 1.0);
                    }
                }
                else if (ix < 0x40038000)
                {
                    id = 2;
                    x = (x - 1.5) / (1.0 + 1.5 * x);
                }
                else
                {
                    id = 3;
                    x = -1.0 / x;
                }
            }
            double z = x * x;
            double w = z * z;
            double s1 = z * (AT0 + w * (AT2 + w * (AT4 + w * (AT6 + w * (AT8 + w * AT10)))));
            double s2 = w * (AT1 + w * (AT3 + w * (AT5 + w * (AT7 + w * AT9))));
            if (id < 0)
                return x - x * (s1 + s2);
            z = AtanHi[id] - (x * (s1 + s2) - AtanLo[id] - x);
            return negative ? -z : z;
        }

        public static double Atan2(double y, double x)
        {
            if (double.IsNaN(x) || double.IsNaN(y))
                return x + y;
            ulong bx = (ulong)BitConverter.DoubleToInt64Bits(x), by = (ulong)BitConverter.DoubleToInt64Bits(y);
            uint ix = (uint)(bx >> 32), lx = (uint)bx, iy = (uint)(by >> 32), ly = (uint)by;
            if (((ix - 0x3ff00000) | lx) == 0)
                return Atan(y);
            uint m = ((iy >> 31) & 1) | ((ix >> 30) & 2);
            ix &= 0x7fffffff;
            iy &= 0x7fffffff;

            if ((iy | ly) == 0)
                return m switch { 0 or 1 => y, 2 => Pi, _ => -Pi };
            if ((ix | lx) == 0)
                return (m & 1) != 0 ? -Pi / 2 : Pi / 2;
            if (ix == 0x7ff00000)
            {
                if (iy == 0x7ff00000)
                    return m switch { 0 => Pi / 4, 1 => -Pi / 4, 2 => 3 * Pi / 4, _ => -3 * Pi / 4 };
                return m switch { 0 => 0.0, 1 => -0.0, 2 => Pi, _ => -Pi };
            }
            // |y/x| > 2^64
            if (ix + (64u << 20) < iy || iy == 0x7ff00000)
                return (m & 1) != 0 ? -Pi / 2 : Pi / 2;

            double z = (m & 2) != 0 && iy + (64u << 20) < ix ? 0.0 : Atan(System.Math.Abs(y / x));
            return m switch
            {
                0 => z,
                1 => -z,
                2 => Pi - (z - PiLo),
                _ => (z - PiLo) - Pi,
            };
        }

        public static double Acos(double x)
        {
            uint hx = High(x);
            uint ix = hx & 0x7fffffff;
            if (ix >= 0x3ff00000)
            {
                // |x| >= 1 or NaN
                uint lx = (uint)BitConverter.DoubleToInt64Bits(x);
                if (((ix - 0x3ff00000) | lx) == 0)
                    return hx >> 31 != 0 ? 2 * Pio2Hi : 0.0;
                return double.NaN;
            }
            if (ix < 0x3fe00000)
            {
                // |x| < 0.5
                if (ix <= 0x3c600000)
                    return Pio2Hi;
                return Pio2Hi - (x - (Pio2Lo - x * AcosR(x * x)));
            }
            double z, s, w;
            if (hx >> 31 != 0)
            {
                // x < -0.5
                z = (1.0 + x) * 0.5;
                s = System.Math.Sqrt(z);
                w = AcosR(z) * s - Pio2Lo;
                return 2 * (Pio2Hi - (s + w));
            }
            // x > 0.5
            z = (1.0 - x) * 0.5;
            s = System.Math.Sqrt(z);
            double df = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(s) & unchecked((long)0xffffffff00000000UL));
            double c = (z - df * df) / (s + df);
            w = AcosR(z) * s + c;
            return 2 * (df + w);
        }

        static uint High(double x) => (uint)((ulong)BitConverter.DoubleToInt64Bits(x) >> 32);

        // sin on [-pi/4, pi/4]; y is the tail of x, tail says whether it counts.
        static double KernelSin(double x, double y, bool tail)
        {
            double z = x * x;
            double w = z * z;
            double r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
            double v = z * x;
            if (!tail)
                return x + v * (S1 + z * r);
            return x - ((z * (0.5 * y - v * r) - y) - v * S1);
        }

        static double KernelCos(double x, double y)
        {
            double z = x * x;
            double w = z * z;
            double r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
            double hz = 0.5 * z;
            w = 1.0 - hz;
            return w + (((1.0 - w) - hz) + (z * r - x * y));
        }

        // x - n pi/2 as y0 + y1, musl's "medium" case for every |x| > pi/4:
        // exact to 2^20 pi/2, beyond that less exact but still the same bits everywhere.
        static int RemPio2(double x, out double y0, out double y1)
        {
            uint ix = High(x) & 0x7fffffff;
            double fn = System.Math.Round(x * InvPio2, MidpointRounding.ToEven);
            int n = (int)fn;
            double r = x - fn * Pio2_1;
            double w = fn * Pio2_1t;
            if (r - w < -Pio4)
            {
                n--;
                fn--;
                r = x - fn * Pio2_1;
                w = fn * Pio2_1t;
            }
            else if (r - w > Pio4)
            {
                n++;
                fn++;
                r = x - fn * Pio2_1;
                w = fn * Pio2_1t;
            }
            y0 = r - w;
            int ex = (int)(ix >> 20);
            int ey = (int)(High(y0) >> 20) & 0x7ff;
            if (ex - ey > 16)
            {
                double t = r;
                w = fn * Pio2_2;
                r = t - w;
                w = fn * Pio2_2t - ((t - r) - w);
                y0 = r - w;
                ey = (int)(High(y0) >> 20) & 0x7ff;
                if (ex - ey > 49)
                {
                    t = r;
                    w = fn * Pio2_3;
                    r = t - w;
                    w = fn * Pio2_3t - ((t - r) - w);
                    y0 = r - w;
                }
            }
            y1 = (r - y0) - w;
            return n;
        }

        static double AcosR(double z)
        {
            double p = z * (PS0 + z * (PS1 + z * (PS2 + z * (PS3 + z * (PS4 + z * PS5)))));
            double q = 1.0 + z * (QS1 + z * (QS2 + z * (QS3 + z * QS4)));
            return p / q;
        }

        const double Pi = 3.1415926535897931160E+00, PiLo = 1.2246467991473531772E-16;
        const double Pio2Hi = 1.57079632679489655800e+00, Pio2Lo = 6.12323399573676603587e-17;
        const double Pio4 = 7.85398163397448278999e-01;
        const double InvPio2 = 6.36619772367581382433e-01;
        const double Pio2_1 = 1.57079632673412561417e+00, Pio2_1t = 6.07710050650619224932e-11;
        const double Pio2_2 = 6.07710050630396597660e-11, Pio2_2t = 2.02226624879595063154e-21;
        const double Pio2_3 = 2.02226624871116645580e-21, Pio2_3t = 8.47842766036889956997e-32;

        const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03,
                     S3 = -1.98412698298579493134e-04, S4 = 2.75573137070700676789e-06,
                     S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;
        const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03,
                     C3 = 2.48015872894767294178e-05, C4 = -2.75573143513906633035e-07,
                     C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;

        static readonly double[] AtanHi =
        {
            4.63647609000806093515e-01, 7.85398163397448278999e-01,
            9.82793723247329054082e-01, 1.57079632679489655800e+00,
        };
        static readonly double[] AtanLo =
        {
            2.26987774529616870924e-17, 3.06161699786838301793e-17,
            1.39033110312309984516e-17, 6.12323399573676603587e-17,
        };
        const double AT0 = 3.33333333333329318027e-01, AT1 = -1.99999999998764832476e-01,
                     AT2 = 1.42857142725034663711e-01, AT3 = -1.11111104054623557880e-01,
                     AT4 = 9.09088713343650656196e-02, AT5 = -7.69187620504482999495e-02,
                     AT6 = 6.66107313738753120669e-02, AT7 = -5.83357013379057348645e-02,
                     AT8 = 4.97687799461593236017e-02, AT9 = -3.65315727442169155270e-02,
                     AT10 = 1.62858201153657823623e-02;

        const double PS0 = 1.66666666666666657415e-01, PS1 = -3.25565818622400915405e-01,
                     PS2 = 2.01212532134862925881e-01, PS3 = -4.00555345006794114027e-02,
                     PS4 = 7.91534994289814532176e-04, PS5 = 3.47933107596021167570e-05;
        const double QS1 = -2.40339491173441421878e+00, QS2 = 2.02094576023350569471e+00,
                     QS3 = -6.88283971605453293030e-01, QS4 = 7.70381505559019352791e-02;
    }
}
