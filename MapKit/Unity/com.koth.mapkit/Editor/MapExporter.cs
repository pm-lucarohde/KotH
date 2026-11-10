using System;
using System.Collections.Generic;
using System.IO;
using KotH.MapFormat;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KotH.MapKit.Editor
{
    /// <summary>Turns a scene into a map package: the markers (info, zone, spawns, bots) into
    /// map.toml, every drawn mesh and terrain in world space with its material into map.glb and the
    /// collision, then <see cref="MapBuilder.Write"/> checks and writes it. Unity's axes become
    /// the game's on the way: z → −z, so the triangles turn round too.</summary>
    public sealed class MapExporter
    {
        readonly List<string> notes = new List<string>();
        readonly MapModel model = new MapModel();
        readonly Dictionary<(Material, MapTemplate, float), int> materials = new Dictionary<(Material, MapTemplate, float), int>();
        readonly Dictionary<int, Vector4> tiling = new Dictionary<int, Vector4>();
        readonly Dictionary<Texture, int> textures = new Dictionary<Texture, int>();
        readonly Dictionary<MapMesh, float[]> lightmapUvs = new Dictionary<MapMesh, float[]>();
        readonly List<MapInfo> infos = new List<MapInfo>();
        readonly List<MapZone> zones = new List<MapZone>();
        readonly List<MapSpawn> spawns = new List<MapSpawn>();
        readonly List<MapBots> bots = new List<MapBots>();

        /// <summary>Exports into mapsRoot/NAME; the result's errors and warnings plus this run's notes.</summary>
        public MapBuilder.Result Export(Scene scene, string mapsRoot, byte[] previewPng, out List<string> notes)
        {
            notes = this.notes;
            foreach (GameObject root in scene.GetRootGameObjects())
                Walk(root, true, null);
            var fail = new MapBuilder.Result();
            if (infos.Count != 1)
                fail.Errors.Add(infos.Count == 0 ? "No Map info component: add one (to any object)." : "More than one Map info component.");
            if (zones.Count != 1)
                fail.Errors.Add(zones.Count == 0 ? "No Map zone component: add one to the zone's object." : "More than one Map zone component.");
            if (bots.Count > 1)
                fail.Errors.Add("More than one Map bots component.");
            if (!fail.Ok)
                return fail;

            MapInfo info = infos[0];
            var manifest = new MapManifest
            {
                Name = info.mapName ?? "",
                Title = string.IsNullOrWhiteSpace(info.title) ? info.mapName : info.title,
                Author = info.author ?? "",
                Description = info.description ?? "",
                Visibility = info.visibility.ToString().ToLowerInvariant(),
            };
            Zone(zones[0], manifest.Zone);
            foreach (MapSpawn s in spawns)
            {
                Vector3 at = Game(s.transform.position);
                var spawn = new MapFormat.MapSpawn { X = at.x, Y = at.y, Z = at.z };
                if (!s.faceZone)
                {
                    // The game's yaw: 0 looks along its −Z (Unity's +Z), 90 along −X.
                    Vector3 f = s.transform.forward;
                    spawn.Yaw = (float)Math.Round(Math.Atan2(-f.x, f.z) * 180.0 / Math.PI, 3);
                }
                manifest.Spawns.Add(spawn);
            }
            if (bots.Count == 1)
                foreach (MapBots.Setting kv in bots[0].settings)
                {
                    if (Array.IndexOf(MapBots.Names, kv.name) < 0)
                        fail.Errors.Add($"Map bots: unknown setting {kv.name}.");
                    manifest.Bots[kv.name ?? ""] = kv.value;
                }
            if (!fail.Ok)
                return fail;

            // An upload's item id stays with the folder: keep it from the last export.
            string folder = Path.Combine(mapsRoot, manifest.Name);
            string old = Path.Combine(folder, MapPackage.ManifestFile);
            if (MapManifest.ValidName(manifest.Name) && File.Exists(old))
                try
                {
                    manifest.WorkshopId = MapManifest.Parse(File.ReadAllText(old)).WorkshopId;
                }
                catch (FormatException)
                {
                }
            return MapBuilder.Write(folder, manifest, model, previewPng,
                                    baked => LightmapAtlas.Run(baked, lightmapUvs, this.notes), info.changeNote);
        }

        /// <summary>A Unity point or direction in the game's axes.</summary>
        public static Vector3 Game(Vector3 v) => new Vector3(v.x, v.y, -v.z);

        static void Zone(MapZone c, MapFormat.MapZone z)
        {
            Vector3 at = Game(c.transform.position), scale = c.transform.lossyScale;
            z.X = at.x;
            z.Y = at.y;
            z.Z = at.z;
            z.SizeX = Math.Abs(scale.x);
            z.SizeY = Math.Abs(scale.y);
            z.SizeZ = Math.Abs(scale.z);
            z.CaptureSeconds = c.captureSeconds;
            z.WinPoints = c.winPoints;
            z.TimeLimitMinutes = c.timeLimitMinutes;
            z.WinPauseSeconds = c.winPauseSeconds;
            z.CountdownSeconds = c.countdownSeconds;
            z.JoinSeconds = c.joinSeconds;
            z.RespawnBelow = c.respawnBelow;
            z.RingInner = c.ringInner;
            z.RingOuter = c.ringOuter;
            z.RingSpacing = c.ringSpacing;
        }

        // Markers count anywhere outside ignored trees; their own meshes never.
        void Walk(GameObject go, bool geometry, MapObject settings)
        {
            if (!go.activeSelf || go.CompareTag("EditorOnly") || go.TryGetComponent(out MapIgnore _))
                return;
            if (go.TryGetComponent(out MapObject own))
                settings = own;
            bool marker = false;
            if (go.TryGetComponent(out MapInfo info))
                infos.Add(info);
            if (go.TryGetComponent(out MapBots bot))
                bots.Add(bot);
            if (go.TryGetComponent(out MapZone zone))
            {
                zones.Add(zone);
                marker = true;
            }
            if (go.TryGetComponent(out MapSpawn spawn))
            {
                spawns.Add(spawn);
                marker = true;
            }
            geometry &= !marker;
            if (geometry)
            {
                if (go.TryGetComponent(out MeshRenderer renderer) && renderer.enabled && go.TryGetComponent(out MeshFilter filter)
                    && filter.sharedMesh != null)
                    AddMesh(go.name, filter.sharedMesh, go.transform.localToWorldMatrix, renderer.sharedMaterials, settings);
                if (go.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.enabled && skinned.sharedMesh != null)
                {
                    // As posed now; the bake carries the scale, the matrix only place and turn.
                    var baked = new Mesh();
                    skinned.BakeMesh(baked, true);
                    notes.Add($"{go.name}: skinned, exported as posed in the editor.");
                    Transform t = go.transform;
                    AddMesh(go.name, baked, Matrix4x4.TRS(t.position, t.rotation, Vector3.one), skinned.sharedMaterials, settings);
                }
                if (go.TryGetComponent(out Terrain terrain) && terrain.enabled && terrain.terrainData != null)
                    TerrainMesh.Add(this, terrain, settings);
            }
            foreach (Transform child in go.transform)
                Walk(child.gameObject, geometry, settings);
        }

        void AddMesh(string name, Mesh mesh, Matrix4x4 world, Material[] mats, MapObject settings)
        {
            Vector3[] vertices, normals;
            Vector2[] uv, uv2;
            try
            {
                vertices = mesh.vertices;
                normals = mesh.normals;
                uv = mesh.uv;
                uv2 = mesh.uv2;
            }
            catch (Exception e) when (e is UnityException || e is ArgumentException)
            {
                notes.Add($"{name}: its mesh cannot be read ({e.Message}); tick Read/Write in the model's import settings.");
                return;
            }
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                string part = mesh.subMeshCount == 1 ? name : $"{name} {sub + 1}";
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                {
                    notes.Add($"{part}: not triangles, skipped.");
                    continue;
                }
                Material material = mats.Length == 0 ? null : mats[Math.Min(sub, mats.Length - 1)];
                Add(part, vertices, normals, uv, uv2, mesh.GetTriangles(sub), world, material, settings, null);
            }
        }

        /// <summary>One drawn part: Unity vertices and triangles (its winding) under a matrix, then
        /// material and settings. Terrain passes its own material, null takes the Unity one.</summary>
        internal void Add(string name, Vector3[] vertices, Vector3[] normals, Vector2[] uv, Vector2[] uv2, int[] indices,
                          Matrix4x4 world, Material material, MapObject settings, MapMaterial own)
        {
            MapTemplate template = settings != null ? settings.template : MapTemplate.Surface;
            int materialIndex = own != null ? AddOwn(own) : MaterialOf(material, template, settings != null ? settings.textureScale : 4f, name);
            Vector4 st = tiling.TryGetValue(materialIndex, out Vector4 t) ? t : new Vector4(1f, 1f, 0f, 0f);
            bool hasNormal = normals != null && normals.Length == vertices.Length;
            bool hasUv = uv != null && uv.Length == vertices.Length;
            bool hasUv2 = uv2 != null && uv2.Length == vertices.Length;

            // Only the vertices the part uses, in first-use order; z → −z turns the winding once,
            // a mirroring matrix once more.
            var remap = new Dictionary<int, int>();
            var used = new List<int>();
            var mesh = new MapMesh { Name = name, Indices = new int[indices.Length - indices.Length % 3] };
            bool mirrored = world.determinant < 0f;
            for (int i = 0; i + 2 < indices.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int v = indices[i + (mirrored ? k : k == 0 ? 0 : 3 - k)];
                    if (!remap.TryGetValue(v, out int to))
                    {
                        remap[v] = to = used.Count;
                        used.Add(v);
                    }
                    mesh.Indices[i + k] = to;
                }
            Matrix4x4 normalMatrix = world.inverse.transpose;
            mesh.Positions = new float[used.Count * 3];
            mesh.Normals = hasNormal ? new float[used.Count * 3] : null;
            mesh.Uv0 = hasUv ? new float[used.Count * 2] : null;
            float[] lightmap = hasUv2 ? new float[used.Count * 2] : null;
            for (int j = 0; j < used.Count; j++)
            {
                int v = used[j];
                Put(mesh.Positions, j, Game(world.MultiplyPoint3x4(vertices[v])));
                if (hasNormal)
                    Put(mesh.Normals, j, Game(normalMatrix.MultiplyVector(normals[v]).normalized));
                if (hasUv)
                {
                    // glTF's V runs down the picture, Unity's up.
                    mesh.Uv0[2 * j] = uv[v].x * st.x + st.z;
                    mesh.Uv0[2 * j + 1] = 1f - (uv[v].y * st.y + st.w);
                }
                if (hasUv2)
                {
                    lightmap[2 * j] = uv2[v].x;
                    lightmap[2 * j + 1] = uv2[v].y;
                }
            }

            mesh.Shadows = (MapShadows)(int)(settings != null ? settings.shadows : MapShadowMode.Baked);
            MapCollisionMode collide = settings != null ? settings.collision : MapCollisionMode.Mesh;
            mesh.Collide = collide == MapCollisionMode.None ? MapCollide.None
                : collide == MapCollisionMode.InvisibleWall ? MapCollide.Only : MapCollide.Mesh;
            mesh.Material = materialIndex;
            if (lightmap != null)
                lightmapUvs[mesh] = lightmap;
            if (mesh.Uv0 == null && model.Materials[materialIndex].Template != "Terrain" && model.Materials[materialIndex].ColorTexture >= 0)
                notes.Add($"{name}: textured but without UVs; use the Terrain template.");
            model.Meshes.Add(mesh);
        }

        static void Put(float[] to, int j, Vector3 v)
        {
            to[3 * j] = v.x;
            to[3 * j + 1] = v.y;
            to[3 * j + 2] = v.z;
        }

        int AddOwn(MapMaterial m)
        {
            model.Materials.Add(m);
            return model.Materials.Count - 1;
        }

        // The Unity material's colour, texture, smoothness and metallic on our template: URP
        // (Lit, Simple Lit) and Built-in (Standard) names; tiling and offset go into the UVs.
        int MaterialOf(Material material, MapTemplate template, float textureScale, string user)
        {
            var key = (material, template, template == MapTemplate.Terrain ? textureScale : 0f);
            if (materials.TryGetValue(key, out int index))
                return index;
            string name = material != null ? material.name : "Default";
            var m = new MapMaterial { Name = $"{name} ({template})", Template = template.ToString(), TextureScale = key.Item3 > 0f ? textureScale : 1f };
            var st = new Vector4(1f, 1f, 0f, 0f);
            if (material != null)
            {
                string colorName = material.HasProperty("_BaseColor") ? "_BaseColor" : material.HasProperty("_Color") ? "_Color" : null;
                if (colorName != null)
                {
                    Color c = material.GetColor(colorName).linear;
                    m.Color = new[] { c.r, c.g, c.b, material.GetColor(colorName).a };
                }
                string smooth = material.HasProperty("_Smoothness") ? "_Smoothness" : material.HasProperty("_Glossiness") ? "_Glossiness" : null;
                if (smooth != null)
                    m.Roughness = Mathf.Clamp01(1f - material.GetFloat(smooth));
                if (material.HasProperty("_Metallic"))
                    m.Metalness = Mathf.Clamp01(material.GetFloat("_Metallic"));
                string map = material.HasProperty("_BaseMap") ? "_BaseMap" : material.HasProperty("_MainTex") ? "_MainTex" : null;
                if (map != null && material.GetTexture(map) is Texture texture)
                {
                    m.ColorTexture = TextureOf(texture, user);
                    Vector2 scale = material.GetTextureScale(map), offset = material.GetTextureOffset(map);
                    st = new Vector4(scale.x, scale.y, offset.x, offset.y);
                }
                if (colorName == null && map == null)
                    notes.Add($"{name}: shader {material.shader.name} has no colour we know; white.");
                if (material.HasProperty("_BumpMap") && material.GetTexture("_BumpMap") != null && template != MapTemplate.Water)
                    notes.Add($"{name}: normal maps are not drawn yet, left out.");
            }
            materials[key] = index = model.Materials.Count;
            tiling[index] = st;
            model.Materials.Add(m);
            return index;
        }

        internal int TextureOf(Texture texture, string user)
        {
            if (textures.TryGetValue(texture, out int index))
                return index;
            byte[] png = TextureExport.Png(texture, out string problem);
            if (png == null)
            {
                notes.Add($"{user}: texture {texture.name}: {problem}; colour only.");
                index = -1;
            }
            else
            {
                index = model.Textures.Count;
                model.Textures.Add(png);
            }
            textures[texture] = index;
            return index;
        }

        internal void Note(string note) => notes.Add(note);
    }
}
