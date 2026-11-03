using System;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>NVIDIA's NGX for Vulkan (DLSS SDK 310.9.16, include/nvsdk_ngx_vk.h), only what DLSS Super
    /// Resolution needs. The loader libnvsdk_ngx.a is linked into the AOT binary (DirectPInvoke); without it
    /// (JIT, Windows for now) every call throws DllNotFoundException and DLSS stays off.</summary>
    internal static unsafe class Ngx
    {
        const string Lib = "nvsdk_ngx";

        public const int Success = 1;
        public const int VersionApi = 0x15;
        public const int FeatureSuperSampling = 1;

        public static bool Failed(int result) => ((uint)result & 0xFFF00000) == 0xBAD00000;

        /// <summary>NVSDK_NGX_PerfQuality_Value.</summary>
        public enum Quality { MaxPerf, Balanced, MaxQuality, UltraPerformance = 3, UltraQuality, Dlaa }

        /// <summary>NVSDK_NGX_DLSS_Feature_Flags.</summary>
        [Flags]
        public enum CreateFlags { None = 0, IsHdr = 1, MvLowRes = 2, MvJittered = 4, DepthInverted = 8, AutoExposure = 64 }

        /// <summary>NVSDK_NGX_DLSS_Hint_Render_Preset: K is DLSS 4's transformer, L and M DLSS 4.5's.</summary>
        public const uint PresetK = 11, PresetL = 12, PresetM = 13;

        [StructLayout(LayoutKind.Sequential)]
        public struct ProjectIdDescription
        {
            public byte* ProjectId;
            public int EngineType;          // NVSDK_NGX_ENGINE_TYPE_CUSTOM = 0
            public byte* EngineVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ApplicationIdentifier
        {
            public int IdentifierType;      // 1: project id
            public ProjectIdDescription Project;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FeatureCommonInfo
        {
            public void** Paths;            // wchar_t const* const*
            public uint PathCount;
            public void* InternalData;
            public void* LoggingCallback;
            public int MinimumLoggingLevel;
            public byte DisableOtherLoggingSinks;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FeatureDiscoveryInfo
        {
            public int SdkVersion;
            public int FeatureId;
            public ApplicationIdentifier Identifier;
            public void* ApplicationDataPath;   // wchar_t*
            public FeatureCommonInfo* FeatureInfo;
        }

        /// <summary>NVSDK_NGX_Resource_VK with an image view (the only kind DLSS takes).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ResourceVk
        {
            public VkImageView ImageView;
            public VkImage Image;
            public VkImageSubresourceRange SubresourceRange;
            public VkFormat Format;
            public uint Width, Height;
            public int Type;                // NVSDK_NGX_RESOURCE_VK_TYPE_VK_IMAGEVIEW = 0
            public byte ReadWrite;
        }

        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_Init_with_ProjectID(byte* projectId, int engineType, byte* engineVersion,
            void* applicationDataPath, VkInstance instance, VkPhysicalDevice physicalDevice, VkDevice device,
            void* getInstanceProcAddr, void* getDeviceProcAddr, FeatureCommonInfo* featureInfo, int sdkVersion);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_Shutdown1(VkDevice device);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_GetCapabilityParameters(void** parameters);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_AllocateParameters(void** parameters);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_DestroyParameters(void* parameters);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_CreateFeature1(VkDevice device, VkCommandBuffer commandBuffer,
            int feature, void* parameters, void** handle);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_ReleaseFeature(void* handle);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_EvaluateFeature_C(VkCommandBuffer commandBuffer, void* handle,
            void* parameters, void* progress);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements(FeatureDiscoveryInfo* info,
            uint* count, VkExtensionProperties** properties);
        [DllImport(Lib)] public static extern int NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements(VkInstance instance,
            VkPhysicalDevice physicalDevice, FeatureDiscoveryInfo* info, uint* count, VkExtensionProperties** properties);

        [DllImport(Lib)] static extern void NVSDK_NGX_Parameter_SetUI(void* p, byte* name, uint value);
        [DllImport(Lib)] static extern void NVSDK_NGX_Parameter_SetI(void* p, byte* name, int value);
        [DllImport(Lib)] static extern void NVSDK_NGX_Parameter_SetF(void* p, byte* name, float value);
        [DllImport(Lib)] static extern void NVSDK_NGX_Parameter_SetVoidPointer(void* p, byte* name, void* value);
        [DllImport(Lib)] static extern int NVSDK_NGX_Parameter_GetUI(void* p, byte* name, uint* value);
        [DllImport(Lib)] static extern int NVSDK_NGX_Parameter_GetI(void* p, byte* name, int* value);
        [DllImport(Lib)] static extern int NVSDK_NGX_Parameter_GetVoidPointer(void* p, byte* name, void** value);

        // Parameter names as NUL-terminated UTF-8, from nvsdk_ngx_defs.h.
        public static void SetUI(void* p, ReadOnlySpan<byte> name, uint value) { fixed (byte* n = name) NVSDK_NGX_Parameter_SetUI(p, n, value); }
        public static void SetI(void* p, ReadOnlySpan<byte> name, int value) { fixed (byte* n = name) NVSDK_NGX_Parameter_SetI(p, n, value); }
        public static void SetF(void* p, ReadOnlySpan<byte> name, float value) { fixed (byte* n = name) NVSDK_NGX_Parameter_SetF(p, n, value); }
        public static void SetPointer(void* p, ReadOnlySpan<byte> name, void* value) { fixed (byte* n = name) NVSDK_NGX_Parameter_SetVoidPointer(p, n, value); }

        public static uint GetUI(void* p, ReadOnlySpan<byte> name)
        {
            uint value = 0;
            fixed (byte* n = name) NVSDK_NGX_Parameter_GetUI(p, n, &value);
            return value;
        }

        public static int GetI(void* p, ReadOnlySpan<byte> name)
        {
            int value = 0;
            fixed (byte* n = name) NVSDK_NGX_Parameter_GetI(p, n, &value);
            return value;
        }

        public static void* GetPointer(void* p, ReadOnlySpan<byte> name)
        {
            void* value = null;
            fixed (byte* n = name) NVSDK_NGX_Parameter_GetVoidPointer(p, n, &value);
            return value;
        }

        /// <summary>wchar_t text: four bytes a character on Linux, two on Windows; free with Marshal.FreeHGlobal.</summary>
        public static void* WideText(string text)
        {
            int size = OperatingSystem.IsWindows() ? 2 : 4;
            byte* buffer = (byte*)Marshal.AllocHGlobal((text.Length + 1) * size);
            for (int i = 0; i <= text.Length; i++)
            {
                uint c = i < text.Length ? text[i] : 0u;
                if (size == 2)
                    ((ushort*)buffer)[i] = (ushort)c;
                else
                    ((uint*)buffer)[i] = c;
            }
            return buffer;
        }

        /// <summary>An extension's name: the first field, 256 bytes of UTF-8.</summary>
        public static string Name(VkExtensionProperties* e) => Marshal.PtrToStringUTF8((nint)e) ?? "";

        // The DLSS SR keys used here (NVSDK_NGX_Parameter_*).
        public static ReadOnlySpan<byte> Available => "SuperSampling.Available\0"u8;
        public static ReadOnlySpan<byte> NeedsUpdatedDriver => "SuperSampling.NeedsUpdatedDriver\0"u8;
        public static ReadOnlySpan<byte> FeatureInitResult => "SuperSampling.FeatureInitResult\0"u8;
        public static ReadOnlySpan<byte> OptimalSettingsCallback => "DLSSOptimalSettingsCallback\0"u8;
        public static ReadOnlySpan<byte> Width => "Width\0"u8;
        public static ReadOnlySpan<byte> Height => "Height\0"u8;
        public static ReadOnlySpan<byte> OutWidth => "OutWidth\0"u8;
        public static ReadOnlySpan<byte> OutHeight => "OutHeight\0"u8;
        public static ReadOnlySpan<byte> PerfQualityValue => "PerfQualityValue\0"u8;
        public static ReadOnlySpan<byte> RtxValue => "RTXValue\0"u8;
        public static ReadOnlySpan<byte> CreationNodeMask => "CreationNodeMask\0"u8;
        public static ReadOnlySpan<byte> VisibilityNodeMask => "VisibilityNodeMask\0"u8;
        public static ReadOnlySpan<byte> CreateFlagsKey => "DLSS.Feature.Create.Flags\0"u8;
        public static ReadOnlySpan<byte> EnableOutputSubrects => "DLSS.Enable.Output.Subrects\0"u8;
        public static ReadOnlySpan<byte> PresetDlaa => "DLSS.Hint.Render.Preset.DLAA\0"u8;
        public static ReadOnlySpan<byte> PresetQuality => "DLSS.Hint.Render.Preset.Quality\0"u8;
        public static ReadOnlySpan<byte> PresetBalanced => "DLSS.Hint.Render.Preset.Balanced\0"u8;
        public static ReadOnlySpan<byte> PresetPerformance => "DLSS.Hint.Render.Preset.Performance\0"u8;
        public static ReadOnlySpan<byte> PresetUltraPerformance => "DLSS.Hint.Render.Preset.UltraPerformance\0"u8;
        public static ReadOnlySpan<byte> Color => "Color\0"u8;
        public static ReadOnlySpan<byte> Output => "Output\0"u8;
        public static ReadOnlySpan<byte> Depth => "Depth\0"u8;
        public static ReadOnlySpan<byte> MotionVectors => "MotionVectors\0"u8;
        public static ReadOnlySpan<byte> JitterX => "Jitter.Offset.X\0"u8;
        public static ReadOnlySpan<byte> JitterY => "Jitter.Offset.Y\0"u8;
        public static ReadOnlySpan<byte> Reset => "Reset\0"u8;
        public static ReadOnlySpan<byte> MvScaleX => "MV.Scale.X\0"u8;
        public static ReadOnlySpan<byte> MvScaleY => "MV.Scale.Y\0"u8;
        public static ReadOnlySpan<byte> RenderWidth => "DLSS.Render.Subrect.Dimensions.Width\0"u8;
        public static ReadOnlySpan<byte> RenderHeight => "DLSS.Render.Subrect.Dimensions.Height\0"u8;
        public static ReadOnlySpan<byte> PreExposure => "DLSS.Pre.Exposure\0"u8;
        public static ReadOnlySpan<byte> ExposureScale => "DLSS.Exposure.Scale\0"u8;
        public static ReadOnlySpan<byte> FrameTime => "FrameTimeDeltaInMsec\0"u8;
        public static ReadOnlySpan<byte> ExposureTextureKey => "ExposureTexture\0"u8;
    }
}
