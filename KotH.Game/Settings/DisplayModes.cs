using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;

namespace KotH.Client.Settings
{
    /// <summary>The sizes the game's screen offers (SDL's display modes, biggest first) and its own
    /// size, for the Resolution setting. <see cref="Window"/> is the game's SDL window, set at the start.</summary>
    public static class DisplayModes
    {
        /// <summary>The game's SDL window handle; 0 before (then the first screen counts).</summary>
        public static nint Window { get; set; }

        [StructLayout(LayoutKind.Sequential)]
        struct Mode { public uint Format; public int W, H, RefreshRate; public nint DriverData; }

        static class Linux
        {
            const string Lib = "libSDL2-2.0.so";
            [DllImport(Lib)] public static extern int SDL_GetWindowDisplayIndex(nint window);
            [DllImport(Lib)] public static extern int SDL_GetNumDisplayModes(int display);
            [DllImport(Lib)] public static extern int SDL_GetDisplayMode(int display, int index, out Mode mode);
            [DllImport(Lib)] public static extern int SDL_GetDesktopDisplayMode(int display, out Mode mode);
        }

        static class Windows
        {
            const string Lib = "SDL2";
            [DllImport(Lib)] public static extern int SDL_GetWindowDisplayIndex(nint window);
            [DllImport(Lib)] public static extern int SDL_GetNumDisplayModes(int display);
            [DllImport(Lib)] public static extern int SDL_GetDisplayMode(int display, int index, out Mode mode);
            [DllImport(Lib)] public static extern int SDL_GetDesktopDisplayMode(int display, out Mode mode);
        }

        static readonly bool OnWindows = OperatingSystem.IsWindows();

        static int Display()
        {
            if (Window == 0)
                return 0;
            int d = OnWindows ? Windows.SDL_GetWindowDisplayIndex(Window) : Linux.SDL_GetWindowDisplayIndex(Window);
            return Math.Max(0, d);
        }

        /// <summary>The screen's own size (the desktop's mode), zero if SDL does not know it.</summary>
        public static Int2 Native()
        {
            try
            {
                int d = Display();
                Mode m;
                int r = OnWindows ? Windows.SDL_GetDesktopDisplayMode(d, out m) : Linux.SDL_GetDesktopDisplayMode(d, out m);
                return r == 0 ? new Int2(m.W, m.H) : Int2.Zero;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return Int2.Zero;
            }
        }

        /// <summary>Every size the screen offers from 1024 wide on, once each, the biggest first.</summary>
        public static List<Int2> Sizes()
        {
            var sizes = new List<Int2>();
            try
            {
                int d = Display();
                int count = OnWindows ? Windows.SDL_GetNumDisplayModes(d) : Linux.SDL_GetNumDisplayModes(d);
                for (int i = 0; i < count; i++)
                {
                    Mode m;
                    int r = OnWindows ? Windows.SDL_GetDisplayMode(d, i, out m) : Linux.SDL_GetDisplayMode(d, i, out m);
                    var size = new Int2(m.W, m.H);
                    if (r == 0 && m.W >= 1024 && !sizes.Contains(size))
                        sizes.Add(size);
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            sizes.Sort((a, b) => b.X != a.X ? b.X.CompareTo(a.X) : b.Y.CompareTo(a.Y));
            return sizes;
        }
    }
}
