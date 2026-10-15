using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using KotH.Probes.Harness;
using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using Stride.BepuPhysics;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Replays thousands of PhysX queries recorded in Unity's arena through
    /// WorldQueries and compares the answers. Measures how far Bepu differs
    /// from PhysX before any motor runs on it, query kind by query kind.
    /// </summary>
    public sealed class WorldQueryProbe : Probe
    {
        protected override string Tag => "wq";

        readonly string dumpPath;

        public WorldQueryProbe(string dumpPath)
        {
            this.dumpPath = dumpPath;
        }

        sealed class Stat
        {
            public int Cases, Agree, HitSetDiffers;
            public readonly List<float> DistDiff = new List<float>();
            public readonly List<float> NormalDiff = new List<float>();
            public readonly List<string> Worst = new List<string>();
        }

        readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();

        Stat S(string kind)
        {
            if (!stats.TryGetValue(kind, out Stat s))
                stats[kind] = s = new Stat();
            return s;
        }

        static readonly Regex Token = new Regex(@"[^;\s]+");

        static float Bits(string cell)
        {
            int bar = cell.IndexOf('|');
            return BitConverter.Int32BitsToSingle(int.Parse(cell.Substring(bar + 1), NumberStyles.HexNumber));
        }

        static Vector3 V(string[] c, int i) => new Vector3(Bits(c[i]), Bits(c[i + 1]), Bits(c[i + 2]));

        struct RefHit
        {
            public string Name;
            public float Dist;
            public Vector3 Normal;
        }

        static List<RefHit> ParseHits(string rest)
        {
            var list = new List<RefHit>();
            MatchCollection m = Token.Matches(rest);
            for (int i = 0; i < m.Count; )
            {
                string name = m[i].Value;
                if (name.Contains("|"))
                {
                    i++;
                    continue;
                }

                var h = new RefHit
                {
                    Name = name,
                    Dist = Bits(m[i + 1].Value),
                    Normal = new Vector3(Bits(m[i + 2].Value), Bits(m[i + 3].Value), Bits(m[i + 4].Value)),
                };
                list.Add(h);
                i += 8;
            }

            return list;
        }

        protected override void Start()
        {
            World.LoadArena();
            WorldQueries q = World.Queries;
            var hits = new QueryHit[16];
            var cols = new CollidableComponent[16];
            CollisionMask mask = WorldQueries.WorldMask;

            foreach (string line in Ref.DumpLines(dumpPath, "queries.txt"))
            {
                if (line.StartsWith("#"))
                    continue;

                string[] c = line.Split(';');
                string kind = c[0];

                if (kind == "ray")
                {
                    Vector3 o = Ref.Pos(V(c, 2)), d = Ref.Pos(V(c, 5));
                    bool refHit = c[9] != "-";
                    bool ours = q.Raycast(o, d, 5f, mask, out QueryHit h);
                    Stat s = S("ray");
                    s.Cases++;
                    if (refHit != ours)
                    {
                        Note(s, $"ray {c[1]} PhysX {(refHit ? c[9] : "-")} Bepu {(ours ? h.Collider.Entity.Name + " " + h.Distance : "-")}");
                        continue;
                    }

                    if (!refHit)
                    {
                        s.Agree++;
                        continue;
                    }

                    if (h.Collider.Entity.Name != c[9])
                    {
                        s.HitSetDiffers++;
                        Note(s, $"ray {c[1]} PhysX {c[9]} Bepu {h.Collider.Entity.Name}");
                        continue;
                    }

                    s.Agree++;
                    Compare(s, $"ray {c[1]} {c[9]}", Bits(c[10]), Ref.Pos(V(c, 11)), h.Distance, h.Normal);
                }
                else if (kind == "sphere" || kind == "capsuleS" || kind == "capsuleC")
                {
                    Vector3 p = Ref.Pos(V(c, 2)), d = Ref.Pos(V(c, 5));
                    int n;
                    if (kind == "sphere")
                    {
                        n = q.SphereCastAll(p, 0.1f, d, 1.5f, mask, hits);
                    }
                    else
                    {
                        float height = kind == "capsuleS" ? 1.8f : 0.9f;
                        Vector3 bottom = UMath.Add(p, UMath.Mul(UMath.Up, 0.4f));
                        Vector3 top = UMath.Add(p, UMath.Mul(UMath.Up, height - 0.4f));
                        n = q.CapsuleCastAll(bottom, top, 0.4f, d, 1.0f, mask, hits);
                    }

                    List<RefHit> reference = ParseHits(string.Join(";", c, 11, c.Length - 11));
                    CompareSets(S(kind), $"{kind} {c[1]}", reference, hits, n);
                }
                else if (kind == "overlap")
                {
                    Vector3 p = Ref.Pos(V(c, 2));
                    float r = Bits(c[5]);
                    int refCount = int.Parse(c[6]);
                    Vector3 bottom = UMath.Add(p, UMath.Mul(UMath.Up, 0.4f));
                    Vector3 top = UMath.Add(p, UMath.Mul(UMath.Up, 1.4f));
                    int n = q.OverlapCapsule(bottom, top, r, mask, cols);
                    Stat s = S(r == 0.4f ? "overlap" : "overlap95");
                    s.Cases++;

                    var refNames = new List<string>(c[7].Length == 0 ? Array.Empty<string>() : c[7].Split(','));
                    var ours = new List<string>();
                    for (int i = 0; i < n; i++)
                        ours.Add(cols[i].Entity.Name);
                    ours.Sort(StringComparer.Ordinal);

                    if (string.Join(",", refNames) != string.Join(",", ours))
                    {
                        s.HitSetDiffers++;
                        Note(s, $"overlap {c[1]} r {r} PhysX [{c[7]}] Bepu [{string.Join(",", ours)}]");
                        continue;
                    }

                    s.Agree++;
                    if (r != 0.4f)
                        continue;

                    // pen NAME True dx|b;dy|b;dz|b depth|b
                    for (int i = 8; i < c.Length; i++)
                    {
                        if (!c[i].StartsWith("pen "))
                            continue;

                        string[] w = c[i].Split(' ');
                        string name = w[1];
                        bool refPen = w[2] == "True";
                        Vector3 refDir = Ref.Pos(new Vector3(Bits(w[3]), Bits(c[i + 1]), Bits(c[i + 2].Split(' ')[0])));
                        float refDepth = Bits(c[i + 2].Split(' ')[1]);

                        CollidableComponent other = null;
                        for (int k = 0; k < n; k++)
                            if (cols[k].Entity.Name == name)
                                other = cols[k];

                        if (other == null)
                            continue;

                        Vector3 centre = UMath.Add(p, UMath.Mul(UMath.Up, 0.9f));
                        bool ourPen = q.ComputePenetration(
                            UMath.Sub(centre, UMath.Mul(UMath.Up, 0.5f)),
                            UMath.Add(centre, UMath.Mul(UMath.Up, 0.5f)), 0.4f, other,
                            out Vector3 dir, out float depth);

                        Stat ps = S("penetration");
                        ps.Cases++;
                        if (refPen != ourPen)
                        {
                            ps.HitSetDiffers++;
                            Note(ps, $"pen {c[1]} {name} PhysX {refPen} {refDepth} Bepu {ourPen} {depth}");
                            continue;
                        }

                        ps.Agree++;
                        if (refPen)
                            Compare(ps, $"pen {c[1]} {name}", refDepth, refDir, depth, dir);
                    }
                }
            }

            FromBelow(q, mask, hits);
            Report();
            Exit(0);
        }

        /// <summary>
        /// The ground is one-sided, and Unity never reported back faces of
        /// a mesh. A ray from under the floor has to pass straight through
        /// it; sweeps from there are only logged, nothing casts them.
        /// </summary>
        void FromBelow(WorldQueries q, CollisionMask mask, QueryHit[] hits)
        {
            Stat s = S("vonunten");
            var rng = new Random(17);
            int sweepsHit = 0;
            for (int i = 0; i < 200; i++)
            {
                Vector3 o = Ref.Pos((float)(rng.NextDouble() * 48 - 24), -0.3f, (float)(rng.NextDouble() * 48 - 24));
                s.Cases++;
                if (q.Raycast(o, UMath.Up, 0.6f, mask, out QueryHit h) && h.Collider.Entity.Name == "Ground")
                {
                    s.HitSetDiffers++;
                    Note(s, $"Strahl von unten bei {Ref.V(o)} trifft den Boden bei {h.Distance:F4}");
                    continue;
                }

                s.Agree++;
                int n = q.SphereCastAll(o, 0.1f, UMath.Up, 0.6f, mask, hits);
                for (int k = 0; k < n; k++)
                    if (hits[k].Collider.Entity.Name == "Ground")
                        sweepsHit++;
            }

            Log($"[wq] Kugelsweeps von unten, die den Boden treffen: {sweepsHit} von 200");
        }

        void CompareSets(Stat s, string label, List<RefHit> reference, QueryHit[] hits, int n)
        {
            s.Cases++;
            var ours = new Dictionary<string, QueryHit>();
            for (int i = 0; i < n; i++)
                ours[hits[i].Collider.Entity.Name] = hits[i];

            bool same = ours.Count == reference.Count;
            foreach (RefHit r in reference)
                same &= ours.ContainsKey(r.Name);

            if (!same)
            {
                s.HitSetDiffers++;
                var refs = new List<string>();
                foreach (RefHit r in reference)
                    refs.Add(r.Name + "@" + r.Dist.ToString("F4", CultureInfo.InvariantCulture));
                var mine = new List<string>();
                foreach (var kv in ours)
                    mine.Add(kv.Key + "@" + kv.Value.Distance.ToString("F4", CultureInfo.InvariantCulture));
                Note(s, $"{label} PhysX [{string.Join(" ", refs)}] Bepu [{string.Join(" ", mine)}]");
                return;
            }

            s.Agree++;
            foreach (RefHit r in reference)
            {
                QueryHit h = ours[r.Name];
                Compare(s, label + " " + r.Name, r.Dist, Ref.Pos(r.Normal), h.Distance, h.Normal);
            }
        }

        void Compare(Stat s, string label, float refDist, Vector3 refNormal, float dist, Vector3 normal)
        {
            float dd = Math.Abs(refDist - dist);
            float na = UMath.Angle(refNormal, normal);
            s.DistDiff.Add(dd);
            s.NormalDiff.Add(na);
            if (dd > 0.005f || na > 1f)
                Note(s, $"{label} dist PhysX {refDist:F5} Bepu {dist:F5} normal PhysX {Ref.V(refNormal)} Bepu {Ref.V(normal)} ({na:F2}°)");
        }

        static void Note(Stat s, string text)
        {
            if (s.Worst.Count < 25)
                s.Worst.Add(text);
        }

        static string Quantiles(List<float> v)
        {
            if (v.Count == 0)
                return "-";
            v.Sort();
            float Q(float f) => v[Math.Min(v.Count - 1, (int)(f * v.Count))];
            return $"p50 {Q(0.5f):G4} p99 {Q(0.99f):G4} max {v[v.Count - 1]:G4}";
        }

        void Report()
        {
            foreach (var kv in stats)
            {
                Stat s = kv.Value;
                Log($"[wq] == {kv.Key}: {s.Agree}/{s.Cases} gleich, {s.HitSetDiffers} andere Treffermenge, "
                    + $"Abstand {Quantiles(s.DistDiff)} m, Normale {Quantiles(s.NormalDiff)} °");
                foreach (string w in s.Worst)
                    Log("[wq]    " + w);
            }
        }
    }
}
