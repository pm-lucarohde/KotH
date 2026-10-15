using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Stride.Core.Mathematics;

namespace KotH.Probes.Harness
{
    /// <summary>
    /// The probes still speak Unity's coordinates: their cases were written
    /// in them, and their logs are compared line by line against the Unity
    /// reference. Everything crosses into Stride here, z and yaw negated,
    /// which is exact in floating point.
    /// </summary>
    public static class Ref
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>A Unity position as a Stride position.</summary>
        public static Vector3 Pos(float x, float y, float z) => new Vector3(x, y, -z);

        public static Vector3 Pos(Vector3 unity) => new Vector3(unity.X, unity.Y, -unity.Z);

        /// <summary>A Stride position or direction back in Unity's terms.</summary>
        public static Vector3 ToUnity(Vector3 stride) => new Vector3(stride.X, stride.Y, -stride.Z);

        /// <summary>A Unity yaw or pitch as the Stride angle.</summary>
        public static float Angle(float unity) => -unity;

        /// <summary>
        /// Lines of a reference dump. The large ones are kept gzipped in
        /// the repo, so name.txt.gz is read when it exists.
        /// </summary>
        public static IEnumerable<string> DumpLines(string dumpPath, string name)
        {
            string gz = Path.Combine(dumpPath, name + ".gz");
            if (!File.Exists(gz))
            {
                foreach (string line in File.ReadLines(Path.Combine(dumpPath, name)))
                    yield return line;
                yield break;
            }

            using var reader = new StreamReader(new GZipStream(File.OpenRead(gz), CompressionMode.Decompress));
            for (string line; (line = reader.ReadLine()) != null; )
                yield return line;
        }

        /// <summary>Unity's Vector3.ToString(): two decimals, invariant.</summary>
        public static string V(Vector3 stride)
        {
            Vector3 u = ToUnity(stride);
            return "(" + u.X.ToString("F2", Invariant) + ", " + u.Y.ToString("F2", Invariant)
                 + ", " + u.Z.ToString("F2", Invariant) + ")";
        }
    }
}
