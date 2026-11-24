using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace KotH.Client.Rendering
{
    /// <summary>The game's icon (Resources/Icon.png, embedded) on its SDL window: title bar and task bar on X11
    /// and Windows, on Wayland through SDL's toplevel icon. The Windows exe carries it as KotH.ico too.</summary>
    public static unsafe class WindowIcon
    {
        // SDL_PIXELFORMAT_ABGR8888: bytes R, G, B, A in memory on little-endian machines, as Skia's Rgba8888.
        const uint Abgr8888 = 0x16762004;

        static class Linux
        {
            const string Lib = "libSDL2-2.0.so";
            [DllImport(Lib)] public static extern nint SDL_CreateRGBSurfaceWithFormatFrom(void* pixels, int w, int h, int depth, int pitch, uint format);
            [DllImport(Lib)] public static extern void SDL_SetWindowIcon(nint window, nint surface);
            [DllImport(Lib)] public static extern void SDL_FreeSurface(nint surface);
        }

        static class Windows
        {
            const string Lib = "SDL2";
            [DllImport(Lib)] public static extern nint SDL_CreateRGBSurfaceWithFormatFrom(void* pixels, int w, int h, int depth, int pitch, uint format);
            [DllImport(Lib)] public static extern void SDL_SetWindowIcon(nint window, nint surface);
            [DllImport(Lib)] public static extern void SDL_FreeSurface(nint surface);
        }

        public static void Set(nint window)
        {
            using Stream png = typeof(WindowIcon).Assembly.GetManifestResourceStream("KotH.Icon.png");
            if (png == null)
                return;
            using var decoded = SKBitmap.Decode(png);
            using var icon = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            if (!decoded.CopyTo(icon, SKColorType.Rgba8888))
                return;
            bool windows = OperatingSystem.IsWindows();
            void* pixels = (void*)icon.GetPixels();
            nint surface = windows
                ? Windows.SDL_CreateRGBSurfaceWithFormatFrom(pixels, icon.Width, icon.Height, 32, icon.RowBytes, Abgr8888)
                : Linux.SDL_CreateRGBSurfaceWithFormatFrom(pixels, icon.Width, icon.Height, 32, icon.RowBytes, Abgr8888);
            if (surface == 0)
                return;
            // SDL copies the pixels: the surface and the bitmap may go right after.
            if (windows)
                Windows.SDL_SetWindowIcon(window, surface);
            else
                Linux.SDL_SetWindowIcon(window, surface);
            if (windows)
                Windows.SDL_FreeSurface(surface);
            else
                Linux.SDL_FreeSurface(surface);
        }
    }
}
