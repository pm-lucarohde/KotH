using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using KotH.Client.Rendering;
using KotH.Client.Steam;

namespace KotH.Client
{
    /// <summary>An assembly may have only one DllImport resolver; this is it for the game and the executable:
    /// Steam's library from next to the executable, SDL as Wayland chose it, everything else as usual.</summary>
    public static class NativeImports
    {
        static readonly HashSet<Assembly> installed = new HashSet<Assembly>();

        public static void Install(Assembly assembly)
        {
            lock (installed)
                if (installed.Add(assembly))
                    NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }

        static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
            => name == Wayland.SdlName ? Wayland.Sdl : SteamApi.Resolve(name);
    }
}
