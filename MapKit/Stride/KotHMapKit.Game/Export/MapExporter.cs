using System;
using System.Collections.Generic;
using System.IO;
using KotH.MapFormat;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;

namespace KotH.MapKit
{
    /// <summary>Turns the running scene into a map package: the markers (info, zone, spawns, bots)
    /// into map.toml, every model's meshes in world space with their materials into map.glb and the
    /// collision, then <see cref="MapBuilder.Write"/> checks and writes it. Reads the compiled scene,
    /// so prefabs, imported files and procedural models all export the same way.</summary>
    public sealed class MapExporter
    {
        readonly IServiceRegistry services;
        readonly ContentManager content;
        readonly List<string> notes = new List<string>();
        readonly MapModel model = new MapModel();
        readonly Dictionary<(Material, MapTemplate, float), int> materials = new Dictionary<(Material, MapTemplate, float), int>();
        readonly Dictionary<Texture, int> textures = new Dictionary<Texture, int>();
        readonly List<MapInfoComponent> infos = new List<MapInfoComponent>();
        readonly List<MapZoneComponent> zones = new List<MapZoneComponent>();
        readonly List<MapSpawnComponent> spawns = new List<MapSpawnComponent>();
        readonly List<MapBotsComponent> bots = new List<MapBotsComponent>();

        public MapExporter(IServiceRegistry services)
        {
            this.services = services;
            content = services.GetService<ContentManager>();
        }

        /// <summary>Exports into mapsRoot/NAME; the result's errors and warnings plus this run's notes.</summary>
        public MapBuilder.Result Export(Scene scene, string mapsRoot, byte[] previewPng, out List<string> notes)
        {
            notes = this.notes;
            Walk(scene);
            var fail = new MapBuilder.Result();
            if (infos.Count != 1)
                fail.Errors.Add(infos.Count == 0 ? "No Map info component: add one (to any entity)." : "More than one Map info component.");
            if (zones.Count != 1)
                fail.Errors.Add(zones.Count == 0 ? "No Map zone component: add one to the zone's entity." : "More than one Map zone component.");
            if (bots.Count > 1)
                fail.Errors.Add("More than one Map bots component.");
            if (!fail.Ok)
                return fail;

            MapInfoComponent info = infos[0];
            var manifest = new MapManifest
            {
                Name = info.Name ?? "",
                Title = string.IsNullOrWhiteSpace(info.Title) ? info.Name : info.Title,
                Author = info.Author ?? "",
                Description = info.Description ?? "",
                Visibility = info.Visibility.ToString().ToLowerInvariant(),
            };
            Zone(zones[0], manifest.Zone);
            foreach (MapSpawnComponent s in spawns)
            {
                Vector3 at = s.Entity.Transform.WorldMatrix.TranslationVector;
                var spawn = new MapSpawn { X = at.X, Y = at.Y, Z = at.Z };
                if (!s.FaceZone)
                {
                    Vector3 f = Vector3.TransformNormal(-Vector3.UnitZ, s.Entity.Transform.WorldMatrix);
                    spawn.Yaw = MathF.Round(MathF.Atan2(-f.X, -f.Z) * 180f / MathF.PI, 3);
                }
                manifest.Spawns.Add(spawn);
            }
            if (bots.Count == 1)
                foreach (KeyValuePair<string, double> kv in bots[0].Settings)
                {
                    if (Array.IndexOf(MapBotsComponent.Names, kv.Key) < 0)
                        fail.Errors.Add($"Map bots: unknown setting {kv.Key}.");
                    manifest.Bots[kv.Key] = kv.Value;
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
            return MapBuilder.Write(folder, manifest, model, previewPng, LightmapUnwrap.Run, info.ChangeNote);
        }

        static void Zone(MapZoneComponent c, MapFormat.MapZone z)
        {
            c.Entity.Transform.WorldMatrix.Decompose(out Vector3 scale, out Quaternion _, out Vector3 at);
            z.X = at.X;
            z.Y = at.Y;
            z.Z = at.Z;
            z.SizeX = Math.Abs(scale.X);
            z.SizeY = Math.Abs(scale.Y);
            z.SizeZ = Math.Abs(scale.Z);
            z.CaptureSeconds = c.CaptureSeconds;
            z.WinPoints = c.WinPoints;
            z.TimeLimitMinutes = c.TimeLimitMinutes;
            z.WinPauseSeconds = c.WinPauseSeconds;
            z.CountdownSeconds = c.CountdownSeconds;
            z.JoinSeconds = c.JoinSeconds;
            z.RespawnBelow = c.RespawnBelow;
            z.RingInner = c.RingInner;
            z.RingOuter = c.RingOuter;
            z.RingSpacing = c.RingSpacing;
        }

        void Walk(Scene scene)
        {
            foreach (Entity e in scene.Entities)
                if (e.Transform.Parent == null)
                    Walk(e, true, null);
            foreach (Scene child in scene.Children)
                Walk(child);
        }

        // Markers count anywhere outside ignored trees; their own models (the kit's gizmos) never.
        void Walk(Entity e, bool geometry, MapObjectComponent settings)
        {
            if (e.Get<MapIgnoreComponent>() != null)
                return;
            settings = e.Get<MapObjectComponent>() ?? settings;
            bool marker = false;
            foreach (EntityComponent c in e.Components)
                switch (c)
                {
                    case MapInfoComponent i: infos.Add(i); break;
                    case MapZoneComponent z: zones.Add(z); marker = true; break;
                    case MapSpawnComponent s: spawns.Add(s); marker = true; break;
                    case MapBotsComponent b: bots.Add(b); break;
                }
            geometry &= !marker;
            if (geometry)
                foreach (EntityComponent c in e.Components)
                    if (c is ModelComponent m && m.Enabled && m.Model != null)
                        AddModel(m, settings);
            foreach (TransformComponent child in e.Transform.Children)
                Walk(child.Entity, geometry, settings);
        }

        void AddModel(ModelComponent component, MapObjectComponent settings)
        {
            Model m = component.Model;
            for (int i = 0; i < m.Meshes.Count; i++)
            {
                Mesh mesh = m.Meshes[i];
                MeshDraw draw = mesh.Draw;
                string name = m.Meshes.Count == 1 ? component.Entity.Name : $"{component.Entity.Name} {i + 1}";
                if (draw == null || draw.PrimitiveType != PrimitiveType.TriangleList || draw.VertexBuffers.Length == 0)
                {
                    notes.Add($"{name}: not a triangle list, skipped.");
                    continue;
                }
                if (mesh.Skinning != null)
                    notes.Add($"{name}: skinned, exported in its rest pose.");
                Matrix world = component.Skeleton != null && mesh.NodeIndex < component.Skeleton.NodeTransformations.Length
                    ? component.Skeleton.NodeTransformations[mesh.NodeIndex].WorldMatrix
                    : component.Entity.Transform.WorldMatrix;
                Material material = component.Materials.TryGetValue(mesh.MaterialIndex, out Material over) && over != null
                    ? over
                    : mesh.MaterialIndex < m.Materials.Count ? m.Materials[mesh.MaterialIndex].Material : null;
                MapMesh exported = Read(name, draw, world);
                if (exported == null)
                    continue;
                exported.Shadows = (MapShadows)(int)(settings?.Shadows ?? MapShadowMode.Baked);
                MapCollisionMode collide = settings?.Collision ?? MapCollisionMode.Mesh;
                exported.Collide = collide == MapCollisionMode.None ? MapCollide.None
                    : collide == MapCollisionMode.InvisibleWall ? MapCollide.Only : MapCollide.Mesh;
                MapTemplate template = settings?.Template ?? MapTemplate.Surface;
                exported.Material = MaterialOf(material, template, settings?.TextureScale ?? 4f, name);
                if (exported.Uv0 == null && template != MapTemplate.Terrain && model.Materials[exported.Material].ColorTexture >= 0)
                    notes.Add($"{name}: textured but without UVs; use the Terrain template.");
                model.Meshes.Add(exported);
            }
        }

        // Positions, normals and UV0 of the drawn range in world space; glTF winding (Stride's is
        // the other way round, a mirroring transform turns it once more).
        MapMesh Read(string name, MeshDraw draw, Matrix world)
        {
            VertexBufferBinding vertices = draw.VertexBuffers[0];
            if (!vertices.Declaration.TryGetElement("POSITION", 0, out VertexElementWithOffset position)
                || position.VertexElement.Format != PixelFormat.R32G32B32_Float)
            {
                notes.Add($"{name}: no float3 positions, skipped.");
                return null;
            }
            bool hasNormal = vertices.Declaration.TryGetElement("NORMAL", 0, out VertexElementWithOffset normal)
                             && normal.VertexElement.Format == PixelFormat.R32G32B32_Float;
            bool hasUv = vertices.Declaration.TryGetElement("TEXCOORD", 0, out VertexElementWithOffset uv)
                         && uv.VertexElement.Format == PixelFormat.R32G32_Float;
            vertices.AsReadable(services, out VertexBufferHelper helper, out int _);
            byte[] bytes = helper.DataOuter;
            int[] indices;
            if (draw.IndexBuffer != null)
            {
                draw.IndexBuffer.AsReadable(services, out IndexBufferHelper indexHelper, out int _);
                indices = indexHelper.To32Bit().Slice(draw.StartLocation, draw.DrawCount).ToArray();
            }
            else
            {
                indices = new int[draw.DrawCount];
                for (int k = 0; k < indices.Length; k++)
                    indices[k] = draw.StartLocation + k;
            }

            // Only the vertices the range uses, in first-use order.
            var remap = new Dictionary<int, int>();
            var used = new List<int>();
            var mesh = new MapMesh { Name = name, Indices = new int[indices.Length - indices.Length % 3] };
            bool mirrored = world.Determinant() < 0f;
            for (int t = 0; t + 2 < indices.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int v = indices[t + (mirrored ? k : k == 0 ? 0 : 3 - k)];
                    if (!remap.TryGetValue(v, out int to))
                    {
                        remap[v] = to = used.Count;
                        used.Add(v);
                    }
                    mesh.Indices[t + k] = to;
                }
            Matrix normalMatrix = Matrix.Transpose(Matrix.Invert(world));
            mesh.Positions = new float[used.Count * 3];
            mesh.Normals = hasNormal ? new float[used.Count * 3] : null;
            mesh.Uv0 = hasUv ? new float[used.Count * 2] : null;
            int stride = vertices.Stride;
            for (int j = 0; j < used.Count; j++)
            {
                int at = vertices.Offset + used[j] * stride;
                Vector3 p = Vector3.TransformCoordinate(Read3(bytes, at + position.Offset), world);
                Put(mesh.Positions, j, p);
                if (hasNormal)
                {
                    Vector3 n = Vector3.TransformNormal(Read3(bytes, at + normal.Offset), normalMatrix);
                    n.Normalize();
                    Put(mesh.Normals, j, n);
                }
                if (hasUv)
                {
                    mesh.Uv0[2 * j] = BitConverter.ToSingle(bytes, at + uv.Offset);
                    mesh.Uv0[2 * j + 1] = BitConverter.ToSingle(bytes, at + uv.Offset + 4);
                }
            }
            return mesh;
        }

        static Vector3 Read3(byte[] b, int at)
            => new Vector3(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));

        static void Put(float[] to, int j, Vector3 v)
        {
            to[3 * j] = v.X;
            to[3 * j + 1] = v.Y;
            to[3 * j + 2] = v.Z;
        }

        // The Stride material's colour, texture, glossiness and metalness on our template.
        int MaterialOf(Material material, MapTemplate template, float textureScale, string user)
        {
            var key = (material, template, template == MapTemplate.Terrain ? textureScale : 0f);
            if (materials.TryGetValue(key, out int index))
                return index;
            string name = material != null && content.TryGetAssetUrl(material, out string url) ? Path.GetFileName(url) : "Default";
            var m = new MapMaterial { Name = $"{name} ({template})", Template = template.ToString(), TextureScale = key.Item3 > 0f ? textureScale : 1f };
            ParameterCollection p = material != null && material.Passes.Count > 0 ? material.Passes[0].Parameters : null;
            if (p != null)
            {
                if (Has(p, MaterialKeys.DiffuseValue))
                {
                    Color4 c = p.Get(MaterialKeys.DiffuseValue);
                    m.Color = new[] { c.R, c.G, c.B, c.A };
                }
                if (Has(p, MaterialKeys.GlossinessValue))
                    m.Roughness = Math.Clamp(1f - p.Get(MaterialKeys.GlossinessValue), 0f, 1f);
                if (Has(p, MaterialKeys.MetalnessValue))
                    m.Metalness = Math.Clamp(p.Get(MaterialKeys.MetalnessValue), 0f, 1f);
                if (p.Get(MaterialKeys.DiffuseMap) is Texture texture)
                    m.ColorTexture = TextureOf(texture, user);
                if (p.Get(MaterialKeys.NormalMap) != null && template != MapTemplate.Water)
                    notes.Add($"{name}: normal maps are not drawn yet, left out.");
            }
            materials[key] = index = model.Materials.Count;
            model.Materials.Add(m);
            return index;
        }

        static bool Has(ParameterCollection p, ParameterKey key)
        {
            foreach (ParameterKeyInfo info in p.ParameterKeyInfos)
                if (info.Key == key)
                    return true;
            return false;
        }

        int TextureOf(Texture texture, string user)
        {
            if (textures.TryGetValue(texture, out int index))
                return index;
            if (!content.TryGetAssetUrl(texture, out string url))
            {
                notes.Add($"{user}: its texture is not an asset; colour only.");
                return textures[texture] = -1;
            }
            string name = url;
            try
            {
                using Image image = TextureReader.Load(content, url);
                byte[] rgba = TextureReader.Rgba(image, out int w, out int h);
                index = model.Textures.Count;
                model.Textures.Add(TextureReader.Png(rgba, w, h));
            }
            catch (InvalidDataException e)
            {
                notes.Add($"Texture {name}: {e.Message}; colour only.");
                index = -1;
            }
            textures[texture] = index;
            return index;
        }
    }
}
