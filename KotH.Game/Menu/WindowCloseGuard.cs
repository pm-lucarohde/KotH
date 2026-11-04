using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotH.Client.Menu
{
    /// <summary>Holds the window open during a running online round: an SDL event filter (Stride sets none)
    /// drops the close request (X, Alt+F4) and SDL_QUIT while <see cref="Blocking"/>, and notes it, so the
    /// pause menu can ask "Leave the running round?". A kill from outside is never stopped.</summary>
    public static unsafe class WindowCloseGuard
    {
        const uint Quit = 0x100, WindowEvent = 0x200;
        const byte WindowClose = 14;   // SDL_WINDOWEVENT_CLOSE

        static volatile bool blocking, asked;
        static bool installed;

        /// <summary>Whether a close request is held back now (the pause menu sets it every frame).</summary>
        public static bool Blocking
        {
            get => blocking;
            set => blocking = value;
        }

        public static void Install()
        {
            if (installed)
                return;
            installed = true;
            try
            {
                delegate* unmanaged[Cdecl]<nint, byte*, int> filter = &Filter;
                if (OperatingSystem.IsWindows())
                    Windows.SDL_SetEventFilter((nint)filter, 0);
                else
                    Linux.SDL_SetEventFilter((nint)filter, 0);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Console.WriteLine($"[window] no close guard: {e.Message}");
            }
        }

        /// <summary>A close request was held back since the last call.</summary>
        public static bool TakeRequest()
        {
            bool was = asked;
            asked = false;
            return was;
        }

        // SDL_Event: the type first; a window event's kind at byte 12. 0 drops the event.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Filter(nint user, byte* e)
        {
            uint type = *(uint*)e;
            bool close = type == Quit || type == WindowEvent && e[12] == WindowClose;
            if (!close || !blocking)
                return 1;
            asked = true;
            return 0;
        }

        static class Linux
        {
            [DllImport("libSDL2-2.0.so")] public static extern void SDL_SetEventFilter(nint filter, nint user);
        }

        static class Windows
        {
            [DllImport("SDL2")] public static extern void SDL_SetEventFilter(nint filter, nint user);
        }
    }
}
