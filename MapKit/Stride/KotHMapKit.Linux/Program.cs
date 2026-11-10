using System;
using System.Runtime.InteropServices;

namespace KotH.MapKit.Linux
{
    static class Program
    {
        const int RtldNow = 0x002, RtldGlobal = 0x100;

        [DllImport("libc", EntryPoint = "dlopen")]
        static extern IntPtr DlOpen(string file, int mode);

        // .NET 11's apphost no longer pulls in libstdc++, which Stride's audio library expects
        // in the global scope: load it so before the engine starts.
        static int Main(string[] args)
        {
            if (DlOpen("libstdc++.so.6", RtldNow | RtldGlobal) == IntPtr.Zero)
                Console.Error.WriteLine("libstdc++.so.6 could not be loaded.");
            return KitGame.Main(args);
        }
    }
}
