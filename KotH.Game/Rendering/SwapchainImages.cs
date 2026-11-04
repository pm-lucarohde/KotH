using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KotH.Client.Rendering.Dlss;
using Stride.Graphics;
using Vortice.Vulkan;

namespace KotH.Client.Rendering
{
    /// <summary>Stride asks for the fewest swapchain images the driver allows (3 here). The compositor holds
    /// one until its next refresh, so acquiring the next image waited up to 0.5 ms in every 16th frame (the
    /// slowest 1 % were 40 % such waits); with 4 none waits. Swaps the device's vkCreateSwapchainKHR, once.</summary>
    public static unsafe class SwapchainImages
    {
        const uint Wanted = 4;
        static delegate* unmanaged<VkDevice, VkSwapchainCreateInfoKHR*, VkAllocationCallbacks*, VkSwapchainKHR*, VkResult> create;

        /// <summary>Right after the device is made, before its first swapchain.</summary>
        public static void Install(GraphicsDevice graphicsDevice)
        {
            if (create != null)
                return;
            VkDeviceApi api = StrideVulkan.DeviceApi(graphicsDevice);
            create = (delegate* unmanaged<VkDevice, VkSwapchainCreateInfoKHR*, VkAllocationCallbacks*, VkSwapchainKHR*, VkResult>)api.vkCreateSwapchainKHR_ptr.Value;
            delegate* unmanaged<VkDevice, VkSwapchainCreateInfoKHR*, VkAllocationCallbacks*, VkSwapchainKHR*, VkResult> hook = &Create;
            // A readonly field Stride reads at each call, as for the pipeline cache.
            Unsafe.AsRef(in api.vkCreateSwapchainKHR_ptr) = new PFN_vkVoidFunction((delegate* unmanaged<void>)hook);
        }

        [UnmanagedCallersOnly]
        static VkResult Create(VkDevice device, VkSwapchainCreateInfoKHR* info, VkAllocationCallbacks* allocator, VkSwapchainKHR* made)
        {
            uint asked = info->minImageCount;
            if (asked < Wanted)
            {
                info->minImageCount = Wanted;
                VkResult more = create(device, info, allocator, made);
                info->minImageCount = asked;
                if (more == VkResult.Success)
                {
                    StartLog.Mark($"swapchain {info->imageExtent.width}x{info->imageExtent.height}, {info->presentMode}, {Wanted} images");
                    return more;
                }
                StartLog.Warn($"swapchain: {Wanted} images refused ({more}), {asked} as Stride asked");
            }
            return create(device, info, allocator, made);
        }
    }
}
