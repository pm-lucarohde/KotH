using System;
using System.IO;
using System.Runtime.InteropServices;
using KotH.MapFormat;

namespace KotH.MapKit
{
    /// <summary>Where exports go: the game's maps folder. Under Wine/Proton (Game Studio on Linux)
    /// that is the Linux user's folder, reached through drive Z:, and the SteamCMD file names the
    /// Linux paths, so the native game and SteamCMD find the package.</summary>
    public static class KitPaths
    {
        public static string MapsFolder()
        {
            string home = OperatingSystem.IsWindows() && Wine() ? UnixHome() : null;
            if (home == null)
                return MapPackage.UserFolder();
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string root = data != null && data.StartsWith("/") ? "Z:" + data : home + @"\.local\share";
            MapBuilder.HostPath = p => p.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? p.Substring(2).Replace('\\', '/') : p;
            return Path.Combine(root.Replace('/', '\\'), "KotH", "maps");
        }

        // WINEHOMEDIR is \??\Z:\home\name; HOME may also be passed through as a Unix path.
        static string UnixHome()
        {
            string wine = Environment.GetEnvironmentVariable("WINEHOMEDIR");
            if (wine != null)
                return wine.StartsWith(@"\??\") ? wine.Substring(4) : wine;
            string home = Environment.GetEnvironmentVariable("HOME");
            return home != null && home.StartsWith("/") ? "Z:" + home.Replace('/', '\\') : null;
        }

        static bool Wine()
        {
            IntPtr ntdll = NativeLibrary.Load("ntdll.dll");
            return NativeLibrary.TryGetExport(ntdll, "wine_get_version", out IntPtr _);
        }
    }
}
