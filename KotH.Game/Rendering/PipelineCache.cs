using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KotH.Client.Rendering.Dlss;
using KotH.Shared.Net;
using Stride.Graphics;
using Vortice.Vulkan;

namespace KotH.Client.Rendering
{
    /// <summary>Stride makes every pipeline without a cache, so a start leaned on the driver's own disk cache
    /// (NVIDIA's stops at about 1 GB, then each start compiled again). The device's pipeline functions are
    /// swapped for ones that pass a cache of our own, kept in ~/.cache/KotH/pipelines.bin for this GPU and
    /// driver (across builds); loaded before the first pipeline, saved once the menu is ready and at exit.</summary>
    public static unsafe class PipelineCache
    {
        static readonly byte[] Magic = "KOTHPC1\n"u8.ToArray();
        const int Largest = 128 << 20; // earlier builds' pipelines pile up; past this it starts fresh

        static delegate* unmanaged<VkDevice, VkPipelineCache, uint, VkGraphicsPipelineCreateInfo*, VkAllocationCallbacks*, VkPipeline*, VkResult> graphics;
        static delegate* unmanaged<VkDevice, VkPipelineCache, uint, VkComputePipelineCreateInfo*, VkAllocationCallbacks*, VkPipeline*, VkResult> compute;
        static VkPipelineCache cache;
        static VkDevice device;
        static VkDeviceApi api;
        static int made, saved;
        static readonly object saving = new object();

        static string FilePath
        {
            get
            {
                string root = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotH", "cache")
                    : Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "KotH");
                return Path.Combine(root, "pipelines.bin");
            }
        }

        /// <summary>Right after the device is made, before Stride's first pipeline; KOTH_PIPELINECACHE=0 leaves it out.</summary>
        public static void Install(GraphicsDevice graphicsDevice)
        {
            if (cache.Handle != 0 || Environment.GetEnvironmentVariable("KOTH_PIPELINECACHE") == "0")
                return;
            long at = StartLog.Now;
            try
            {
                device = StrideVulkan.Device(graphicsDevice);
                api = StrideVulkan.DeviceApi(graphicsDevice);
                byte[] initial = Load(graphicsDevice, out string why);
                VkPipelineCache made;
                fixed (byte* data = initial)
                {
                    var info = new VkPipelineCacheCreateInfo { initialDataSize = (ulong)(initial?.Length ?? 0), pInitialData = data };
                    VkResult result = api.vkCreatePipelineCache(device, &info, null, &made);
                    if (result != VkResult.Success && initial != null)
                    {
                        why = $"refused by the driver ({result})";
                        info.initialDataSize = 0;
                        info.pInitialData = null;
                        result = api.vkCreatePipelineCache(device, &info, null, &made);
                    }
                    if (result != VkResult.Success)
                    {
                        StartLog.Warn($"pipeline cache: none ({result})");
                        return;
                    }
                }
                cache = made;
                graphics = (delegate* unmanaged<VkDevice, VkPipelineCache, uint, VkGraphicsPipelineCreateInfo*, VkAllocationCallbacks*, VkPipeline*, VkResult>)
                    api.vkCreateGraphicsPipelines_ptr.Value;
                compute = (delegate* unmanaged<VkDevice, VkPipelineCache, uint, VkComputePipelineCreateInfo*, VkAllocationCallbacks*, VkPipeline*, VkResult>)
                    api.vkCreateComputePipelines_ptr.Value;
                // Readonly fields Stride reads at each call, as for the device hook.
                delegate* unmanaged<VkDevice, VkPipelineCache, uint, VkGraphicsPipelineCreateInfo*, VkAllocationCallbacks*, VkPipeline*, VkResult> g = &CreateGraphics;
                delegate* unmanaged<VkDevice, VkPipelineCache, uint, VkComputePipelineCreateInfo*, VkAllocationCallbacks*, VkPipeline*, VkResult> c = &CreateCompute;
                Unsafe.AsRef(in api.vkCreateGraphicsPipelines_ptr) = new PFN_vkVoidFunction((delegate* unmanaged<void>)g);
                Unsafe.AsRef(in api.vkCreateComputePipelines_ptr) = new PFN_vkVoidFunction((delegate* unmanaged<void>)c);
                StartLog.Took("pipeline cache", at, why);
            }
            catch (Exception e)
            {
                StartLog.Warn($"pipeline cache: none ({e.GetType().Name}: {e.Message})");
            }
        }

        [UnmanagedCallersOnly]
        static VkResult CreateGraphics(VkDevice d, VkPipelineCache c, uint count, VkGraphicsPipelineCreateInfo* infos, VkAllocationCallbacks* allocator, VkPipeline* pipelines)
        {
            Interlocked.Add(ref made, (int)count);
            return graphics(d, c.Handle == 0 ? cache : c, count, infos, allocator, pipelines);
        }

        [UnmanagedCallersOnly]
        static VkResult CreateCompute(VkDevice d, VkPipelineCache c, uint count, VkComputePipelineCreateInfo* infos, VkAllocationCallbacks* allocator, VkPipeline* pipelines)
        {
            Interlocked.Add(ref made, (int)count);
            return compute(d, c.Handle == 0 ? cache : c, count, infos, allocator, pipelines);
        }

        /// <summary>Writes the cache if pipelines were made since the last save: off the game thread (the driver
        /// locks the cache itself), or at once at exit, before the device goes.</summary>
        public static void Save(bool now)
        {
            if (cache.Handle == 0 || Volatile.Read(ref made) == saved)
                return;
            if (now)
                Write();
            else
                Task.Run(Write);
        }

        static void Write()
        {
            lock (saving)
            {
                int count = Volatile.Read(ref made);
                if (count == saved || cache.Handle == 0)
                    return;
                long at = StartLog.Now;
                try
                {
                    var get = (delegate* unmanaged<VkDevice, VkPipelineCache, nuint*, void*, VkResult>)api.vkGetPipelineCacheData_ptr.Value;
                    nuint size = 0;
                    if (get(device, cache, &size, null) != VkResult.Success || size == 0)
                        return;
                    byte[] data = new byte[(int)size];
                    fixed (byte* p = data)
                        if (get(device, cache, &size, p) is not (VkResult.Success or VkResult.Incomplete))
                            return;
                    byte[] build = Encoding.ASCII.GetBytes(NetProtocol.BuildText);
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string temporary = path + ".new";
                    using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
                    {
                        file.Write(Magic);
                        file.Write(BitConverter.GetBytes(build.Length));
                        file.Write(build);
                        file.Write(data, 0, (int)size);
                    }
                    File.Move(temporary, path, overwrite: true);
                    saved = count;
                    StartLog.Took($"pipeline cache saved, {count} pipelines this run, {size / 1024} KiB", at);
                }
                catch (Exception e)
                {
                    StartLog.Warn($"pipeline cache not saved: {e.Message}");
                }
            }
        }

        // The driver's data from the file, or null with the reason: none, unreadable, too large, another GPU or driver.
        static byte[] Load(GraphicsDevice graphicsDevice, out string why)
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                why = "new";
                return null;
            }
            try
            {
                byte[] all = File.ReadAllBytes(path);
                int at = Magic.Length + 4;
                int length = all.Length >= at ? BitConverter.ToInt32(all, Magic.Length) : -1;
                if (!all.AsSpan(0, Math.Min(all.Length, Magic.Length)).SequenceEqual(Magic) || length < 0 || all.Length < at + length + 32)
                {
                    why = "dropped, unreadable";
                    return null;
                }
                if (all.Length > Largest)
                {
                    why = $"dropped, {all.Length >> 20} MB (starts fresh)";
                    return null;
                }
                // Another build's pipelines stay: the driver keys each by its shaders and state.
                string build = Encoding.ASCII.GetString(all, at, length) == NetProtocol.BuildText ? "this build" : "an earlier build";
                byte[] data = all[(at + length)..];
                // VkPipelineCacheHeaderVersionOne: size, version, vendor, device, then the cache UUID.
                VkPhysicalDeviceProperties properties;
                StrideVulkan.InstanceApi(StrideVulkan.FactoryOf(null, false)).vkGetPhysicalDeviceProperties(StrideVulkan.PhysicalDevice(graphicsDevice), &properties);
                bool same = BitConverter.ToUInt32(data, 8) == properties.vendorID && BitConverter.ToUInt32(data, 12) == properties.deviceID
                            && new ReadOnlySpan<byte>(properties.pipelineCacheUUID, 16).SequenceEqual(data.AsSpan(16, 16));
                why = same ? $"loaded {data.Length / 1024} KiB from {build}" : "dropped, another GPU or driver";
                return same ? data : null;
            }
            catch (Exception e)
            {
                why = $"dropped, unreadable ({e.Message})";
                return null;
            }
        }

        /// <summary>At exit, before the device goes: saved, then destroyed.</summary>
        public static void Shutdown()
        {
            Save(now: true);
            lock (saving)
            {
                if (cache.Handle == 0)
                    return;
                api.vkDestroyPipelineCache(device, cache, null);
                cache = default;
            }
        }
    }
}
