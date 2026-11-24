using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using KotH.Client.Rendering.Dlss;
using Silk.NET.Core.Loader;
using Stride.Graphics;
using Vortice.Vulkan;

namespace KotH.Client.Rendering
{
    /// <summary>A native Wayland window on Linux. The bundled SDL knows only X11, so in a Wayland session the
    /// system's (or Steam runtime's) libSDL2-2.0.so.0 serves Stride and the game, and Stride's Vulkan instance
    /// (made with the X11 surface extension only) is made again with Wayland's too. Whatever is missing keeps
    /// X11 through XWayland; so does KOTH_X11=1.</summary>
    public static unsafe class Wayland
    {
        public const string SdlName = "libSDL2-2.0.so";
        const string SystemSdl = "libSDL2-2.0.so.0";

        static int native = -1;

        /// <summary>The system's SDL once it serves, else 0 (the bundled one loads).</summary>
        public static nint Sdl { get; private set; }

        /// <summary>Whether SDL's video runs on Wayland; known once the window exists, false before.</summary>
        public static bool Native
        {
            get
            {
                if (native < 0 && Sdl != 0)
                {
                    byte* name = ((delegate* unmanaged<byte*>)NativeLibrary.GetExport(Sdl, "SDL_GetCurrentVideoDriver"))();
                    if (name != null)
                        native = Marshal.PtrToStringUTF8((nint)name) == "wayland" ? 1 : 0;
                }
                return native == 1;
            }
        }

        /// <summary>Before anything loads SDL or Vulkan makes a device; the executable's DllImports follow too.</summary>
        public static void Prepare(Assembly executable)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
                Environment.GetEnvironmentVariable("KOTH_X11") == "1")
                return;
            long at = StartLog.Now;
            if (!NativeLibrary.TryLoad(SystemSdl, out nint system))
            {
                StartLog.Took("wayland", at, "no system SDL, X11");
                return;
            }
            if (!HasDriver(system, "wayland"))
            {
                NativeLibrary.Free(system);
                StartLog.Took("wayland", at, "system SDL without Wayland, X11");
                return;
            }
            Sdl = system;
            // Silk's loader (Stride's window and input) tries this name first; a second SDL in the process would break both.
            ((DefaultPathResolver)PathResolver.Default).Resolvers.Insert(0, name => name == SdlName ? [SystemSdl] : []);
            NativeImports.Install(typeof(Wayland).Assembly);
            NativeImports.Install(executable);

            string result = ExtendInstance();
            // Without the extension a Wayland window gets no Vulkan surface: the system SDL then stays on X11.
            if (Environment.GetEnvironmentVariable("SDL_VIDEODRIVER") == null)
                SetEnv("SDL_VIDEODRIVER", result == null ? "wayland,x11" : "x11", 1);
            StartLog.Took("wayland", at, result ?? "system SDL, Vulkan instance with VK_KHR_wayland_surface");
        }

        static bool HasDriver(nint library, string driver)
        {
            var count = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "SDL_GetNumVideoDrivers");
            var name = (delegate* unmanaged<int, byte*>)NativeLibrary.GetExport(library, "SDL_GetVideoDriver");
            for (int i = 0, n = count(); i < n; i++)
                if (Marshal.PtrToStringUTF8((nint)name(i)) == driver)
                    return true;
            return false;
        }

        /// <summary>Stride's instance again, as Stride makes it but with Wayland's surface extension (and X11's
        /// that are there), its adapters pointed at the new instance's devices. Null when done, else why not.</summary>
        static string ExtendInstance()
        {
            object factory;
            try
            {
                factory = StrideVulkan.FactoryOf(null, false);
            }
            catch (Exception e)
            {
                return $"no Vulkan ({e.Message}), X11";
            }
            uint count = 0;
            Vulkan.vkEnumerateInstanceExtensionProperties(&count, null);
            var properties = new VkExtensionProperties[count];
            fixed (VkExtensionProperties* p = properties)
                Vulkan.vkEnumerateInstanceExtensionProperties(&count, p);
            var available = new HashSet<string>();
            for (int i = 0; i < count; i++)
                fixed (byte* name = properties[i].extensionName)
                    available.Add(Marshal.PtrToStringUTF8((nint)name));
            if (!available.Contains("VK_KHR_surface") || !available.Contains("VK_KHR_wayland_surface"))
                return "no VK_KHR_wayland_surface, X11";

            var names = new List<nint>();
            foreach (string wanted in new[] { "VK_KHR_surface", "VK_KHR_wayland_surface", "VK_KHR_xlib_surface", "VK_KHR_xcb_surface" })
                if (available.Contains(wanted))
                    names.Add(Marshal.StringToCoTaskMemUTF8(wanted));
            nint engine = Marshal.StringToCoTaskMemUTF8("Stride");
            try
            {
                nint* array = stackalloc nint[names.Count];
                for (int i = 0; i < names.Count; i++)
                    array[i] = names[i];
                var application = new VkApplicationInfo { pEngineName = (byte*)engine, apiVersion = new VkVersion(1, 3, 0) };
                var info = new VkInstanceCreateInfo
                {
                    pApplicationInfo = &application,
                    enabledExtensionCount = (uint)names.Count,
                    ppEnabledExtensionNames = (byte**)array,
                };
                VkInstance created;
                VkResult made = Vulkan.vkCreateInstance(&info, null, &created);
                if (made != VkResult.Success)
                    return $"vkCreateInstance {made}, X11";
                VkInstanceApi api = Vulkan.GetApi(created);
                if (!Repoint(created, api))
                {
                    api.vkDestroyInstance(created, null);
                    return "devices differ, X11";
                }
                ref VkInstance instance = ref StrideVulkan.Instance(factory);
                ref VkInstanceApi instanceApi = ref StrideVulkan.InstanceApi(factory);
                instanceApi.vkDestroyInstance(instance, null);
                instance = created;
                instanceApi = api;
                return null;
            }
            finally
            {
                foreach (nint name in names)
                    Marshal.FreeCoTaskMem(name);
                Marshal.FreeCoTaskMem(engine);
            }
        }

        /// <summary>Each adapter's device from the new instance, found by Stride's enumeration index and
        /// checked by vendor and device id; nothing changes unless all match.</summary>
        static bool Repoint(VkInstance instance, VkInstanceApi api)
        {
            uint count = 0;
            api.vkEnumeratePhysicalDevices(instance, &count, null);
            var devices = new VkPhysicalDevice[count];
            fixed (VkPhysicalDevice* p = devices)
                api.vkEnumeratePhysicalDevices(instance, &count, p);
            ReadOnlySpan<GraphicsAdapter> adapters = GraphicsAdapterFactory.Adapters;
            for (int i = 0; i < adapters.Length; i++)
            {
                int index = StrideVulkan.AdapterIndex(adapters[i]);
                if ((uint)index >= count)
                    return false;
                VkPhysicalDeviceProperties properties;
                api.vkGetPhysicalDeviceProperties(devices[index], &properties);
                if (properties.vendorID != adapters[i].DriverInfo.VendorId || properties.deviceID != adapters[i].DriverInfo.DeviceId)
                    return false;
            }
            for (int i = 0; i < adapters.Length; i++)
                StrideVulkan.AdapterDevice(adapters[i]) = devices[StrideVulkan.AdapterIndex(adapters[i])];
            return true;
        }

        // .NET changes only its own copy of the environment; SDL reads libc's.
        [DllImport("libc", EntryPoint = "setenv")]
        static extern int SetEnv(string name, string value, int overwrite);
    }
}
