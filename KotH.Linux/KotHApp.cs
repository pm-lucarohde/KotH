using System;
using System.Runtime.InteropServices;
using Stride.Engine;

StdCxxPreload.Run();

using var game = new Game();
game.Run();

/// <summary>
/// The .NET 11 apphost and NativeAOT binaries no longer pull in libstdc++,
/// but Stride's libstrideaudio.so expects its symbols in the global scope.
/// Loading it RTLD_GLOBAL before the engine starts restores that.
/// </summary>
static class StdCxxPreload
{
    const int RtldNow = 0x002;
    const int RtldGlobal = 0x100;

    [DllImport("libc", EntryPoint = "dlopen")]
    static extern IntPtr DlOpen(string file, int mode);

    public static void Run()
    {
        if (DlOpen("libstdc++.so.6", RtldNow | RtldGlobal) == IntPtr.Zero)
            Console.Error.WriteLine("libstdc++.so.6 konnte nicht geladen werden, Audio fällt aus.");
    }
}
