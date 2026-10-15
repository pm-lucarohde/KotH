using System;
using System.Globalization;
using System.IO;
using KotH.Probes.Harness;
using KotH.Shared.Mathematics;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Checks UMath's rotations bit for bit against Unity's, dumped over a
    /// grid of pitch and yaw. Every Unity value is mirrored into Stride
    /// first; the mirror itself is exact, so any differing bit is a formula
    /// that rounds differently.
    /// </summary>
    public sealed class RotationProbe : Probe
    {
        protected override string Tag => "rot";

        readonly string dumpPath;

        public RotationProbe(string dumpPath)
        {
            this.dumpPath = dumpPath;
        }

        static float Bits(string cell)
        {
            int bar = cell.IndexOf('|');
            return BitConverter.Int32BitsToSingle(int.Parse(cell.Substring(bar + 1), NumberStyles.HexNumber));
        }

        static Quaternion MirrorQ(string[] c, int i)
            => new Quaternion(-Bits(c[i]), -Bits(c[i + 1]), Bits(c[i + 2]), Bits(c[i + 3]));

        static Vector3 MirrorV(string[] c, int i)
            => new Vector3(Bits(c[i]), Bits(c[i + 1]), -Bits(c[i + 2]));

        // Plus and minus zero count as equal: the mirror flips the sign of
        // a zero term, and nothing downstream tells them apart.
        static bool Same(float a, float b) => a == b || BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        static bool Same(Quaternion a, Quaternion b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z) && Same(a.W, b.W);

        static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);

        static int Ulp(float a, float b)
        {
            int ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b);
            if (ia < 0) ia = int.MinValue - ia;
            if (ib < 0) ib = int.MinValue - ib;
            return Math.Abs(ia - ib);
        }

        static float Diff(Vector3 a, Vector3 b)
            => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

        static int Ulp(Quaternion a, Quaternion b)
            => Math.Max(Math.Max(Ulp(a.X, b.X), Ulp(a.Y, b.Y)), Math.Max(Ulp(a.Z, b.Z), Ulp(a.W, b.W)));

        static int Ulp(Vector3 a, Vector3 b)
            => Math.Max(Ulp(a.X, b.X), Math.Max(Ulp(a.Y, b.Y), Ulp(a.Z, b.Z)));

        sealed class Count
        {
            public int Cases, Equal, MaxUlp;
            public string First;

            public void Add(bool same, int ulp, string what)
            {
                Cases++;
                if (same)
                {
                    Equal++;
                    return;
                }

                MaxUlp = Math.Max(MaxUlp, ulp);
                First ??= what;
            }
        }

        protected override void Start()
        {
            var euler = new Count();
            var eulerYaw = new Count();
            var mono = new Count();
            var rotate = new Count();
            float maxRotate = 0f, maxChain = 0f;
            var chain = new Count();
            bool yawOk = true;

            foreach (string line in Ref.DumpLines(dumpPath, "rotations.txt"))
            {
                if (line.StartsWith("#"))
                    continue;

                string[] c = line.Split(';');
                if (c[0] == "dummyYaw")
                {
                    Quaternion rot = MirrorQ(c, 3);
                    float want = Ref.Angle(Bits(c[1]));
                    float got = UMath.YawOf(rot);
                    yawOk = Same(want, got);
                    Log($"[rot] YawOf Dummy: Unity {Bits(c[1])} gespiegelt {want} UMath {got} {(yawOk ? "bitgleich" : "ABWEICHUNG")}");
                    continue;
                }

                if (c[0] == "playerYaw")
                    continue;

                float pitch = Ref.Angle(Bits(c[0])), yaw = Ref.Angle(Bits(c[1]));
                Quaternion q = MirrorQ(c, 2), qy = MirrorQ(c, 6);
                Vector3 fwd = MirrorV(c, 10), right = MirrorV(c, 13);
                Vector3 yFwd = MirrorV(c, 16), yRight = MirrorV(c, 19);
                string at = $"Nick {Bits(c[0])} Gier {Bits(c[1])}";

                Quaternion e = UMath.Euler(pitch, yaw);
                euler.Add(Same(e, q), Ulp(e, q), at + $" Unity {q} UMath {e}");

                Quaternion ey = UMath.EulerYaw(yaw);
                eulerYaw.Add(Same(ey, qy), Ulp(ey, qy), at + $" Unity {qy} UMath {ey}");

                // Rotate alone, fed Unity's own quaternions: exact under
                // Mono's rounding, a few bits off in plain float.
                Vector3 ma = RotateMono(q, UMath.Forward), mb = RotateMono(q, UMath.Right);
                Vector3 mya = RotateMono(qy, UMath.Forward), myb = RotateMono(qy, UMath.Right);
                mono.Add(Same(ma, fwd) && Same(mb, right) && Same(mya, yFwd) && Same(myb, yRight),
                         Math.Max(Math.Max(Ulp(ma, fwd), Ulp(mb, right)), Math.Max(Ulp(mya, yFwd), Ulp(myb, yRight))),
                         at + $" vorn Unity {fwd} Mono {ma}");

                Vector3 a = UMath.Rotate(q, UMath.Forward), b = UMath.Rotate(q, UMath.Right);
                rotate.Add(Same(a, fwd) && Same(b, right), Math.Max(Ulp(a, fwd), Ulp(b, right)), at);
                maxRotate = Math.Max(maxRotate, Math.Max(Diff(a, fwd), Diff(b, right)));

                // What the motor really computes: Euler, then Rotate.
                Vector3 ca = UMath.Rotate(e, UMath.Forward), cy = UMath.Rotate(ey, UMath.Forward);
                chain.Add(Same(ca, fwd) && Same(cy, yFwd), Math.Max(Ulp(ca, fwd), Ulp(cy, yFwd)), at);
                maxChain = Math.Max(maxChain, Math.Max(Diff(ca, fwd), Diff(cy, yFwd)));
            }

            bool ok = yawOk;
            ok &= Report("Euler(Nick, Gier)", euler);
            ok &= Report("EulerYaw(Gier)", eulerYaw);
            ok &= Report("Rotate mit Monos Rundung", mono);

            // Plain float may differ from the editor's reference in the last
            // bits only; anything near a millimetre would be a real error.
            Report("Rotate in float", rotate);
            Report("Euler dann Rotate in float", chain);
            Log($"[rot] float gegen Mono: größter Unterschied {maxRotate:G3} bzw. {maxChain:G3}");
            ok &= maxRotate < 1e-6f && maxChain < 1e-6f;
            Log(ok ? "[rot] ROTATIONEN STIMMEN: Euler bitgleich, Rotate bis auf Monos Rundung" : "[rot] ROTATIONEN WEICHEN AB");
            Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Rotate the way Unity's editor Mono ran it: locals rounded to
        /// float, every expression summed in double. Proves the formula is
        /// Unity's; only the rounding differs.
        /// </summary>
        static Vector3 RotateMono(Quaternion r, Vector3 p)
        {
            float num = r.X * 2f, num2 = r.Y * 2f, num3 = r.Z * 2f;
            float num4 = r.X * num, num5 = r.Y * num2, num6 = r.Z * num3;
            float num7 = r.X * num2, num8 = r.X * num3, num9 = r.Y * num3;
            float num10 = r.W * num, num11 = r.W * num2, num12 = r.W * num3;
            return new Vector3(
                (float)((1.0 - ((double)num5 + num6)) * p.X + ((double)num7 - num12) * p.Y + ((double)num8 + num11) * p.Z),
                (float)(((double)num7 + num12) * p.X + (1.0 - ((double)num4 + num6)) * p.Y + ((double)num9 - num10) * p.Z),
                (float)(((double)num8 - num11) * p.X + ((double)num9 + num10) * p.Y + (1.0 - ((double)num4 + num5)) * p.Z));
        }

        bool Report(string name, Count c)
        {
            Log($"[rot] {name}: {c.Equal}/{c.Cases} bitgleich, größte Abweichung {c.MaxUlp} ULP");
            if (c.First != null)
                Log($"[rot]    erste Abweichung: {c.First}");
            return c.Equal == c.Cases;
        }
    }
}
