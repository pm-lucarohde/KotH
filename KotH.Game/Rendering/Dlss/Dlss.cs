using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Stride.Graphics;
using Vortice.Vulkan;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>DLSS's modes as the game offers them; Off renders natively with FXAA.</summary>
    public enum DlssMode { Off, Dlaa, Quality, Balanced, Performance, UltraPerformance }

    /// <summary>DLSS Super Resolution on this machine: Prepare hooks the device creation before Run, Start
    /// initialises NGX on Stride's device and asks whether DLSS runs here (Ready), only once a mode is wanted
    /// (a start without DLSS skips NGX). Off without an NVIDIA RTX card, the loader or the DLSS library.</summary>
    public static unsafe class Dlss
    {
        // Any fixed GUID identifies a custom engine's project to NGX.
        const string ProjectId = "6b6f7468-0d15-4c55-8a4e-2d6b6f746801";

        static Ngx.FeatureDiscoveryInfo* discovery;
        static Ngx.FeatureCommonInfo* common;
        static void* capabilities;
        static VkDevice device;
        static GraphicsDevice waiting;
        static DlssMode mode;
        static bool tried;

        /// <summary>Whether DLSS can run: NGX started and the feature is available.</summary>
        public static bool Ready { get; private set; }

        /// <summary>Whether it may: Ready, or not started yet on a device that has its extensions (for the menu).</summary>
        public static bool Possible => Ready || !tried && VulkanHook.Extended;

        /// <summary>Why not, for the log and the F3 overlay.</summary>
        public static string Failure { get; private set; } = "not started";

        /// <summary>The mode chosen (settings, --dlss, KOTH_DLSS); the first one other than Off starts NGX.</summary>
        public static DlssMode Mode
        {
            get => mode;
            set
            {
                mode = value;
                if (value != DlssMode.Off && !tried && waiting != null)
                    Start(waiting);
            }
        }

        /// <summary>The own sharpening after DLSS, 0 to 1 (DLSS 4 has none); KOTH_DLSS_SHARPNESS, default 0.5.</summary>
        public static float Sharpness { get; set; } = Env("KOTH_DLSS_SHARPNESS") is { } s ? Math.Clamp(s, 0f, 1f) : 0.5f;

        /// <summary>Whether frames go through DLSS now.</summary>
        public static bool Active => Ready && Mode != DlssMode.Off;

        public static DlssMode ParseMode(string text) => text?.ToLowerInvariant() switch
        {
            "dlaa" => DlssMode.Dlaa,
            "quality" or "1" => DlssMode.Quality,
            "balanced" => DlssMode.Balanced,
            "performance" => DlssMode.Performance,
            "ultra" or "ultraperformance" => DlssMode.UltraPerformance,
            _ => DlssMode.Off,
        };

        internal static Ngx.Quality QualityOf(DlssMode mode) => mode switch
        {
            DlssMode.Dlaa => Ngx.Quality.Dlaa,
            DlssMode.Balanced => Ngx.Quality.Balanced,
            DlssMode.Performance => Ngx.Quality.MaxPerf,
            DlssMode.UltraPerformance => Ngx.Quality.UltraPerformance,
            _ => Ngx.Quality.MaxQuality,
        };

        /// <summary>Luca: the DLSS 4 transformer (K) up to Balanced, DLSS 4.5's M and L below, as NVIDIA's
        /// defaults; KOTH_DLSS_PRESET=K|L|M forces one for every mode, to compare.</summary>
        internal static uint PresetOf(DlssMode mode) => Environment.GetEnvironmentVariable("KOTH_DLSS_PRESET")?.ToUpperInvariant() switch
        {
            "K" => Ngx.PresetK,
            "L" => Ngx.PresetL,
            "M" => Ngx.PresetM,
            _ => mode switch { DlssMode.Performance => Ngx.PresetM, DlssMode.UltraPerformance => Ngx.PresetL, _ => Ngx.PresetK },
        };

        static float? Env(string name) => float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                                                           System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null;

        /// <summary>Test aid: the sign DLSS gets the jitter with (KOTH_DLSS_JITTER=1 or -1). +1, the
        /// projection's own offset, is right: -1 left a fine grid on still pictures.</summary>
        internal static float JitterSign { get; } = Env("KOTH_DLSS_JITTER") is < 0f ? -1f : 1f;

        /// <summary>Before the game's Run: the device must be made with DLSS's extensions.</summary>
        public static void Prepare() => VulkanHook.Install();

        // NGX's own description of this app; kept for the process, NGX may hold the pointers.
        static Ngx.FeatureDiscoveryInfo* Discovery()
        {
            if (discovery != null)
                return discovery;
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotH", "ngx");
            Directory.CreateDirectory(data);
            common = (Ngx.FeatureCommonInfo*)NativeMemory.AllocZeroed((nuint)sizeof(Ngx.FeatureCommonInfo));
            void** paths = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
            paths[0] = Ngx.WideText(AppContext.BaseDirectory);
            common->Paths = paths;
            common->PathCount = 1;
            discovery = (Ngx.FeatureDiscoveryInfo*)NativeMemory.AllocZeroed((nuint)sizeof(Ngx.FeatureDiscoveryInfo));
            discovery->SdkVersion = Ngx.VersionApi;
            discovery->FeatureId = Ngx.FeatureSuperSampling;
            discovery->Identifier.IdentifierType = 1;
            discovery->Identifier.Project.ProjectId = (byte*)Marshal.StringToCoTaskMemUTF8(ProjectId);
            discovery->Identifier.Project.EngineVersion = (byte*)Marshal.StringToCoTaskMemUTF8("4.4");
            discovery->ApplicationDataPath = Ngx.WideText(data);
            discovery->FeatureInfo = common;
            return discovery;
        }

        /// <summary>The device extensions DLSS needs on this GPU; empty if it cannot run here.</summary>
        internal static List<string> DeviceExtensions(VkInstance instance, VkPhysicalDevice physical)
        {
            var names = new List<string>();
            uint count = 0;
            VkExtensionProperties* list = null;
            int result = Ngx.NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements(Discovery(), &count, &list);
            if (!Ngx.Failed(result) && count > 0)
            {
                var instanceNames = new List<string>();
                for (uint i = 0; i < count; i++)
                    instanceNames.Add(Ngx.Name(list + i));
                Console.WriteLine($"[dlss] instance extensions wanted: {string.Join(", ", instanceNames)}");
            }
            count = 0;
            list = null;
            result = Ngx.NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements(instance, physical, Discovery(), &count, &list);
            if (Ngx.Failed(result))
            {
                Failure = $"no device requirements (0x{result:X8})";
                return names;
            }
            for (uint i = 0; i < count; i++)
                names.Add(Ngx.Name(list + i));
            return names;
        }

        /// <summary>After the device is made: NGX starts on it now if a mode is wanted, else with the first one.</summary>
        public static void Offer(GraphicsDevice graphics)
        {
            waiting = graphics;
            if (mode != DlssMode.Off)
                Start(graphics);
            else
                Console.WriteLine("[dlss] NGX starts with the first mode chosen");
        }

        // NGX on the device, and whether DLSS runs here; any failure leaves it off.
        static void Start(GraphicsDevice graphics)
        {
            if (tried)
                return;
            tried = true;
            long at = StartLog.Now;
            try
            {
                if (!VulkanHook.Extended)
                {
                    Failure = Failure == "not started" ? "device without DLSS extensions" : Failure;
                    Console.WriteLine($"[dlss] off: {Failure}");
                    return;
                }
                object factory = StrideVulkan.FactoryOf(null, false);
                VkInstance instance = StrideVulkan.Instance(factory);
                device = StrideVulkan.Device(graphics);
                Ngx.FeatureDiscoveryInfo* d = Discovery();
                int result = Ngx.NVSDK_NGX_VULKAN_Init_with_ProjectID(d->Identifier.Project.ProjectId, 0, d->Identifier.Project.EngineVersion,
                    d->ApplicationDataPath, instance, StrideVulkan.PhysicalDevice(graphics), device, null, null, common, Ngx.VersionApi);
                if (Ngx.Failed(result))
                {
                    Failure = $"NGX init failed (0x{result:X8})";
                    Console.WriteLine($"[dlss] off: {Failure}");
                    return;
                }
                void* parameters = null;
                result = Ngx.NVSDK_NGX_VULKAN_GetCapabilityParameters(&parameters);
                if (Ngx.Failed(result))
                {
                    Failure = $"no capabilities (0x{result:X8})";
                    Console.WriteLine($"[dlss] off: {Failure}");
                    return;
                }
                capabilities = parameters;
                if (Ngx.GetI(parameters, Ngx.NeedsUpdatedDriver) != 0)
                    Failure = "driver too old";
                else if (Ngx.GetI(parameters, Ngx.Available) == 0)
                    Failure = $"not available (init result 0x{Ngx.GetI(parameters, Ngx.FeatureInitResult):X8})";
                else
                {
                    Ready = true;
                    Failure = null;
                }
                Console.WriteLine(Ready ? "[dlss] available" : $"[dlss] off: {Failure}");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Failure = "NGX loader missing";
                Console.WriteLine($"[dlss] off: {Failure}");
            }
            catch (Exception e)
            {
                Ready = false;
                Failure = $"{e.GetType().Name}: {e.Message}";
                Console.WriteLine($"[dlss] off: {Failure}");
            }
            StartLog.Took("dlss start", at, Ready ? "ready" : Failure);
        }

        /// <summary>Before the device goes: NGX lets go of it (else the process crashes on exit).</summary>
        public static void Shutdown()
        {
            if (capabilities == null)
                return;
            Ready = false;
            Ngx.NVSDK_NGX_VULKAN_DestroyParameters(capabilities);
            capabilities = null;
            Ngx.NVSDK_NGX_VULKAN_Shutdown1(device);
        }

        // The common heights the render size steps down, highest first.
        static readonly int[] Heights = [2160, 1440, 1080, 720, 480, 360];

        /// <summary>Luca: the render size steps down the common heights, not NVIDIA's factors. Quality the highest
        /// at most 75 % (1440 to 1080, 1080 to 720), Performance a half, Ultra Performance a third, Balanced
        /// halfway between Quality and Performance (down to 60 px), DLAA the output's; the width keeps the aspect.</summary>
        internal static void RenderSize(DlssMode mode, int width, int height, out int renderWidth, out int renderHeight)
        {
            int quality = height * 2 / 3;
            foreach (int h in Heights)
                if (h * 4 <= height * 3)
                {
                    quality = h;
                    break;
                }
            int performance = height / 2;
            int rows = mode switch
            {
                DlssMode.Quality => quality,
                DlssMode.Balanced => Math.Max(performance, (quality + performance) / 2 / 60 * 60),
                DlssMode.Performance => performance,
                DlssMode.UltraPerformance => height / 3,
                _ => height,
            };
            renderHeight = Math.Clamp(rows, 1, height);
            renderWidth = Math.Clamp((int)Math.Round(width * (double)renderHeight / height), 1, width);
        }
    }
}
