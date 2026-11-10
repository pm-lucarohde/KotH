using System;
using KotH.MapFormat;
using UnityEngine;

namespace KotH.MapKit.Editor
{
    /// <summary>A Unity terrain as one mesh: its heightmap (at most 513 samples a side), the first
    /// layer's texture projected from the sides (Terrain template, the layer's tile size), and
    /// the grid itself as lightmap UVs. Trees, details and holes stay behind.</summary>
    static class TerrainMesh
    {
        const int MaxSamples = 513;

        public static void Add(MapExporter exporter, Terrain terrain, MapObject settings)
        {
            TerrainData data = terrain.terrainData;
            int res = data.heightmapResolution;
            int step = Math.Max(1, (res - 1 + MaxSamples - 2) / (MaxSamples - 1));
            int n = (res - 1) / step + 1;
            float[,] heights = data.GetHeights(0, 0, res, res);
            Vector3 size = data.size;
            var vertices = new Vector3[n * n];
            var normals = new Vector3[n * n];
            var uv = new Vector2[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int x = Math.Min(i * step, res - 1), z = Math.Min(j * step, res - 1);
                    float u = (float)x / (res - 1), v = (float)z / (res - 1);
                    vertices[j * n + i] = new Vector3(u * size.x, heights[z, x] * size.y, v * size.z);
                    normals[j * n + i] = data.GetInterpolatedNormal(u, v);
                    uv[j * n + i] = new Vector2(u, v);
                }
            // Two triangles a cell, facing up in Unity's winding.
            var indices = new int[(n - 1) * (n - 1) * 6];
            int t = 0;
            for (int j = 0; j + 1 < n; j++)
                for (int i = 0; i + 1 < n; i++)
                {
                    int a = j * n + i, b = a + n, c = a + n + 1, d = a + 1;
                    indices[t++] = a;
                    indices[t++] = b;
                    indices[t++] = c;
                    indices[t++] = a;
                    indices[t++] = c;
                    indices[t++] = d;
                }

            TerrainLayer[] layers = data.terrainLayers;
            TerrainLayer layer = layers.Length > 0 ? layers[0] : null;
            var material = new MapMaterial
            {
                Name = $"{terrain.name} (Terrain)",
                Template = settings != null ? settings.template.ToString() : "Terrain",
                TextureScale = layer != null ? Math.Max(0.01f, layer.tileSize.x) : 4f,
                Roughness = layer != null ? Mathf.Clamp01(1f - layer.smoothness) : 0.8f,
                Metalness = layer != null ? Mathf.Clamp01(layer.metallic) : 0f,
            };
            if (layer != null && layer.diffuseTexture != null)
                material.ColorTexture = exporter.TextureOf(layer.diffuseTexture, terrain.name);
            if (layers.Length > 1)
                exporter.Note($"{terrain.name}: only the first terrain layer is drawn.");
            if (data.treeInstanceCount > 0 || data.detailPrototypes.Length > 0)
                exporter.Note($"{terrain.name}: trees and details are left out; place trees as objects.");
            if (step > 1)
                exporter.Note($"{terrain.name}: heightmap {res}² exported at every {step}th sample.");
            exporter.Add(terrain.name, vertices, normals, uv, uv, indices, Matrix4x4.Translate(terrain.transform.position),
                         null, settings, material);
        }
    }
}
