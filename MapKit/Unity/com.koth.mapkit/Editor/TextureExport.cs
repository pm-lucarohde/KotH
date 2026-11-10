using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace KotH.MapKit.Editor
{
    /// <summary>A texture as an 8-bit RGBA PNG: PNG and JPG sources are decoded on the CPU, every
    /// other kind is drawn into an sRGB target and read back (needs the editor's graphics).</summary>
    static class TextureExport
    {
        public static byte[] Png(Texture texture, out string problem)
        {
            problem = null;
            string path = AssetDatabase.GetAssetPath(texture);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg") && File.Exists(path))
            {
                var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (decoded.LoadImage(File.ReadAllBytes(path)))
                        return decoded.EncodeToPNG();
                }
                finally
                {
                    Object.DestroyImmediate(decoded);
                }
            }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                problem = $"{ext} needs the editor with graphics (not -nographics)";
                return null;
            }
            return Draw(texture, texture.width, texture.height);
        }

        /// <summary>Draws a texture into an sRGB target and reads it back as a PNG.</summary>
        public static byte[] Draw(Texture texture, int width, int height)
        {
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture before = RenderTexture.active;
            var read = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(texture, target);
                return Read(target, read);
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(read);
            }
        }

        /// <summary>A render target's pixels as a PNG.</summary>
        public static byte[] Read(RenderTexture target, Texture2D into)
        {
            RenderTexture.active = target;
            into.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            into.Apply();
            return into.EncodeToPNG();
        }
    }
}
