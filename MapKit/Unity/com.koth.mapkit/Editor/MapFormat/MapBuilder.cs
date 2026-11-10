using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KotH.MapFormat
{
    /// <summary>Turns a kit's scene (manifest + meshes in world space) into a package folder: checks
    /// what would make the map unplayable, writes map.glb, collision.bin, map.toml, preview.png
    /// and, beside the folder, NAME.workshop.vdf for SteamCMD. Shared by every kit.</summary>
    public static class MapBuilder
    {
        /// <summary>The light's voxel cube: a map fits in this many metres on every axis.</summary>
        public const float MaxExtent = 256f;

        /// <summary>Above this many solid triangles loading and the bots' grid get slow.</summary>
        public const int CollisionWarn = 2_000_000;

        /// <summary>The app the workshop items belong to (Steam's test app until KotH has its own).</summary>
        public static uint AppId = 480;

        /// <summary>Turns a folder into the path SteamCMD sees (the Stride kit under Wine: Z:\x to /x).</summary>
        public static Func<string, string> HostPath = p => p;

        public sealed class Result
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public MapPackage Package;
            public string Vdf;
            public bool Ok => Errors.Count == 0;
        }

        /// <summary>Lays out the lightmap UVs (Uv1) of the baked meshes in one atlas; the kit's own
        /// unwrapper (Stride kit: Prowl.Unwrapper, Unity: its own). Null: baked meshes need Uv1.</summary>
        public delegate void Unwrap(List<MapMesh> baked);

        public static Result Write(string folder, MapManifest manifest, MapModel model, byte[] previewPng, Unwrap unwrap,
                                   string changeNote = "")
        {
            var r = new Result();
            if (!MapManifest.ValidName(manifest.Name))
                r.Errors.Add($"Map name \"{manifest.Name}\": only letters, digits, '-' and '_' (1 to 64).");
            if (manifest.Spawns.Count == 0)
                r.Errors.Add("No spawn point: place at least one.");
            MapZone z = manifest.Zone;
            if (z.SizeX <= 0 || z.SizeY <= 0 || z.SizeZ <= 0)
                r.Errors.Add("The zone needs a size above 0 on every axis.");

            // The solid: every mesh but the decoration, in one soup.
            var positions = new List<float>();
            var triangles = new List<int>();
            foreach (MapMesh m in model.Meshes)
            {
                if (m.Collide == MapCollide.None)
                    continue;
                int first = positions.Count / 3;
                positions.AddRange(m.Positions);
                foreach (int i in m.Indices)
                    triangles.Add(first + i);
            }
            MapCollision collision = MapCollision.FromTriangles(positions, triangles);
            int solid = collision.TriangleCount;
            if (solid == 0)
                r.Errors.Add("Nothing is solid: the players would fall forever.");
            else if (solid > CollisionWarn)
                r.Warnings.Add($"{solid:N0} solid triangles: loading and the bots get slow; simplify or mark decoration Collision None.");

            for (int i = 0; i < manifest.Spawns.Count; i++)
            {
                MapSpawn s = manifest.Spawns[i];
                float? below = GroundBelow(collision, s.X, s.Y + 0.5f, s.Z);
                if (below == null || s.Y - below.Value > 3f)
                    r.Errors.Add($"Spawn {i + 1} at ({s.X:F1}, {s.Y:F1}, {s.Z:F1}) has no ground within 3 m below.");
            }
            if (solid > 0 && GroundBelow(collision, z.X, z.Y + z.SizeY / 2, z.Z) == null)
                r.Warnings.Add("Nothing solid below the zone's centre: is it floating?");
            if (z.RespawnBelow > z.Y - z.SizeY / 2)
                r.Warnings.Add($"Respawn height {z.RespawnBelow} m is above the zone's floor: standing in it respawns.");

            // Every axis within the light's cube.
            float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
            foreach (MapMesh m in model.Meshes)
                for (int i = 0; i < m.Positions.Length; i++)
                {
                    min[i % 3] = Math.Min(min[i % 3], m.Positions[i]);
                    max[i % 3] = Math.Max(max[i % 3], m.Positions[i]);
                }
            for (int k = 0; k < 3 && model.Meshes.Count > 0; k++)
                if (max[k] - min[k] > MaxExtent)
                    r.Errors.Add($"The map is {max[k] - min[k]:F0} m along {"XYZ"[k]}; at most {MaxExtent:F0} m fit the light.");

            foreach (MapMesh m in model.Meshes)
            {
                if (m.Indices.Length % 3 != 0 || m.Positions.Length % 3 != 0)
                    r.Errors.Add($"{m.Name}: broken triangles.");
                if (m.Material < 0 || m.Material >= model.Materials.Count)
                    r.Errors.Add($"{m.Name}: no material.");
            }
            foreach (MapMaterial mat in model.Materials)
                if (Array.IndexOf(MapMaterial.Templates, mat.Template) < 0)
                    r.Errors.Add($"Material {mat.Name}: unknown template {mat.Template} (Surface, Terrain, Leaves, Water).");
            if (!r.Ok)
                return r;

            // The lightmap atlas for the baked meshes that are drawn.
            var baked = new List<MapMesh>();
            foreach (MapMesh m in model.Meshes)
                if (m.Collide != MapCollide.Only && m.Shadows == MapShadows.Baked)
                    baked.Add(m);
            bool missing = baked.Exists(m => m.Uv1 == null);
            if (missing && unwrap != null)
                unwrap(baked);
            else if (missing)
                foreach (MapMesh m in baked)
                    if (m.Uv1 == null)
                    {
                        m.Shadows = MapShadows.Live;
                        r.Warnings.Add($"{m.Name}: no lightmap UVs, its shadow is live.");
                    }

            var drawn = new MapModel();
            drawn.Materials.AddRange(model.Materials);
            drawn.Textures.AddRange(model.Textures);
            foreach (MapMesh m in model.Meshes)
                if (m.Collide != MapCollide.Only)
                    drawn.Meshes.Add(m);

            Directory.CreateDirectory(folder);
            using (FileStream f = File.Create(Path.Combine(folder, MapPackage.ModelFile)))
                drawn.Write(f);
            using (FileStream f = File.Create(Path.Combine(folder, MapPackage.CollisionFile)))
                collision.Write(f);
            File.WriteAllText(Path.Combine(folder, MapPackage.ManifestFile), manifest.ToString(), new UTF8Encoding(false));
            string preview = Path.Combine(folder, MapPackage.PreviewFile);
            if (previewPng != null)
                File.WriteAllBytes(preview, previewPng);
            r.Package = MapPackage.Open(folder);
            r.Vdf = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + ".workshop.vdf";
            File.WriteAllText(r.Vdf, Vdf(r.Package, changeNote, ReadPublished(r.Vdf)), new UTF8Encoding(false));
            return r;
        }

        /// <summary>The height of the highest solid triangle straight below a point, null if none.</summary>
        public static float? GroundBelow(MapCollision c, float x, float y, float z)
        {
            float? best = null;
            float[] v = c.Vertices;
            foreach (MapCollision.Chunk chunk in c.Chunks)
            {
                int[] t = chunk.Triangles;
                for (int i = 0; i < t.Length; i += 3)
                {
                    int a = 3 * t[i], b = 3 * t[i + 1], d = 3 * t[i + 2];
                    // Barycentric in the XZ plane, then the height on the triangle.
                    double x1 = v[b] - v[a], z1 = v[b + 2] - v[a + 2], x2 = v[d] - v[a], z2 = v[d + 2] - v[a + 2];
                    double det = x1 * z2 - x2 * z1;
                    if (Math.Abs(det) < 1e-12)
                        continue;
                    double px = x - v[a], pz = z - v[a + 2];
                    double u = (px * z2 - x2 * pz) / det, w = (x1 * pz - px * z1) / det;
                    if (u < 0 || w < 0 || u + w > 1)
                        continue;
                    float h = (float)(v[a + 1] + u * (v[b + 1] - v[a + 1]) + w * (v[d + 1] - v[a + 1]));
                    if (h <= y && (best == null || h > best.Value))
                        best = h;
                }
            }
            return best;
        }

        /// <summary>The SteamCMD item file: `workshop_build_item PATH` uploads the folder.</summary>
        public static string Vdf(MapPackage p, string changeNote, ulong published)
        {
            MapManifest m = p.Manifest;
            ulong item = m.WorkshopId != 0 ? m.WorkshopId : published;
            int visibility = m.Visibility == "public" ? 0 : m.Visibility == "friends" ? 1 : m.Visibility == "unlisted" ? 3 : 2;
            var sb = new StringBuilder();
            sb.Append("\"workshopitem\"\n{\n");
            void Key(string k, string v) => sb.Append("\t\"").Append(k).Append("\"\t\t\"").Append(Escape(v)).Append("\"\n");
            Key("appid", AppId.ToString(CultureInfo.InvariantCulture));
            Key("publishedfileid", item.ToString(CultureInfo.InvariantCulture));
            Key("contentfolder", HostPath(p.Folder));
            if (File.Exists(p.PreviewPath))
                Key("previewfile", HostPath(p.PreviewPath));
            Key("visibility", visibility.ToString(CultureInfo.InvariantCulture));
            Key("title", m.Title);
            Key("description", m.Description);
            Key("changenote", changeNote ?? "");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The item id SteamCMD wrote back into an existing vdf after its first upload, else 0.</summary>
        public static ulong ReadPublished(string vdf)
        {
            if (!File.Exists(vdf))
                return 0;
            foreach (string line in File.ReadAllLines(vdf))
            {
                string[] parts = line.Split('"');
                if (parts.Length >= 4 && parts[1] == "publishedfileid" && ulong.TryParse(parts[3], out ulong id))
                    return id;
            }
            return 0;
        }

        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }
}
