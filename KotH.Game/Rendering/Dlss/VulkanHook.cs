using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>Stride 4.4 makes its Vulkan device with the swapchain extension only. Before the game
    /// creates it, its vkCreateDevice pointer is swapped for one that also asks for what DLSS needs; if
    /// the device refuses, it is made as Stride wanted and DLSS stays off (Extended false).</summary>
    internal static unsafe class VulkanHook
    {
        static delegate* unmanaged<VkPhysicalDevice, VkDeviceCreateInfo*, VkAllocationCallbacks*, VkDevice*, VkResult> original;
        static VkInstance instance;

        /// <summary>Whether the device got DLSS's extensions.</summary>
        public static bool Extended { get; private set; }

        /// <summary>Hooks the device creation; once, before the game's Run.</summary>
        public static void Install()
        {
            if (original != null)
                return;
            try
            {
                object factory = StrideVulkan.FactoryOf(null, false);
                instance = StrideVulkan.Instance(factory);
                VkInstanceApi api = StrideVulkan.InstanceApi(factory);
                original = (delegate* unmanaged<VkPhysicalDevice, VkDeviceCreateInfo*, VkAllocationCallbacks*, VkDevice*, VkResult>)
                    api.vkCreateDevice_ptr.Value;
                delegate* unmanaged<VkPhysicalDevice, VkDeviceCreateInfo*, VkAllocationCallbacks*, VkDevice*, VkResult> hook = &CreateDevice;
                // A readonly field Stride reads at each call; written once, before the device exists.
                System.Runtime.CompilerServices.Unsafe.AsRef(in api.vkCreateDevice_ptr) = new PFN_vkVoidFunction((delegate* unmanaged<void>)hook);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[dlss] no Vulkan hook: {e.Message}");
            }
        }

        [UnmanagedCallersOnly]
        static VkResult CreateDevice(VkPhysicalDevice physical, VkDeviceCreateInfo* info, VkAllocationCallbacks* allocator, VkDevice* device)
        {
            List<string> wanted;
            try
            {
                wanted = Dlss.DeviceExtensions(instance, physical);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[dlss] off: {e.GetType().Name}");
                return original(physical, info, allocator, device);
            }
            if (wanted.Count == 0)
                return original(physical, info, allocator, device);
            Console.WriteLine($"[dlss] device extensions wanted: {string.Join(", ", wanted)}");

            // Stride's names, then the missing ones, as one array of C strings.
            var names = new List<nint>();
            var have = new HashSet<string>();
            for (uint i = 0; i < info->enabledExtensionCount; i++)
            {
                names.Add((nint)info->ppEnabledExtensionNames[i]);
                have.Add(Marshal.PtrToStringUTF8((nint)info->ppEnabledExtensionNames[i]) ?? "");
            }
            var owned = new List<nint>();
            foreach (string name in wanted)
                if (have.Add(name))
                {
                    nint text = Marshal.StringToCoTaskMemUTF8(name);
                    owned.Add(text);
                    names.Add(text);
                }
            nint* array = stackalloc nint[names.Count];
            for (int i = 0; i < names.Count; i++)
                array[i] = names[i];

            VkDeviceCreateInfo extended = *info;
            extended.enabledExtensionCount = (uint)names.Count;
            extended.ppEnabledExtensionNames = (byte**)array;
            bool addresses = wanted.Contains("VK_KHR_buffer_device_address") || wanted.Contains("VK_EXT_buffer_device_address");
            VkPhysicalDeviceVulkan12Features* features12 = Find12(info->pNext);
            VkBool32 before = features12 != null ? features12->bufferDeviceAddress : VkBool32.False;
            if (addresses && features12 != null)
                features12->bufferDeviceAddress = VkBool32.True;

            VkResult result = original(physical, &extended, allocator, device);
            if (result != VkResult.Success)
            {
                Console.WriteLine($"[dlss] device refused the extensions ({result}), DLSS off");
                if (features12 != null)
                    features12->bufferDeviceAddress = before;
                result = original(physical, info, allocator, device);
            }
            else
            {
                Extended = true;
                Console.WriteLine($"[dlss] device extensions: {string.Join(", ", wanted)}");
            }
            foreach (nint text in owned)
                Marshal.FreeCoTaskMem(text);
            return result;
        }

        static VkPhysicalDeviceVulkan12Features* Find12(void* next)
        {
            for (var s = (VkBaseInStructure*)next; s != null; s = s->pNext)
                if (s->sType == VkStructureType.PhysicalDeviceVulkan12Features)
                    return (VkPhysicalDeviceVulkan12Features*)s;
            return null;
        }
    }
}
