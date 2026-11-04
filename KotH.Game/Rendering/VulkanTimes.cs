using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using KotH.Client.Rendering.Dlss;
using Stride.Graphics;
using Vortice.Vulkan;

namespace KotH.Client.Rendering
{
    /// <summary>Test aid for the frame log: the device's submit, present, wait and object functions are swapped
    /// for ones that add up how long each call took, on the game thread and on all others apart, read and
    /// cleared once a frame (Take).</summary>
    public static unsafe class VulkanTimes
    {
        public enum Call
        {
            Submit, Present, FrameFence, Acquire, WaitSemaphores, AllocateSets, UpdateSets, CreateDescriptorPool,
            ResetDescriptorPool, BeginCommandBuffer, EndCommandBuffer, AllocateCommandBuffers, ResetCommandPool,
            CreateCommandPool, AllocateMemory, CreateBuffer, MapMemory, BeginRendering, PipelineBarrier, BindSets, Count,
        }

        const int N = (int)Call.Count;
        static readonly long[] ticks = new long[2 * N];
        static readonly int[] calls = new int[2 * N];
        static int gameThread;
        static delegate* unmanaged<VkQueue, uint, VkSubmitInfo2*, VkFence, VkResult> submit;
        static delegate* unmanaged<VkQueue, VkPresentInfoKHR*, VkResult> present;
        static delegate* unmanaged<VkDevice, uint, VkFence*, VkBool32, ulong, VkResult> fences;
        static delegate* unmanaged<VkDevice, VkSwapchainKHR, ulong, VkSemaphore, VkFence, uint*, VkResult> acquire;
        static delegate* unmanaged<VkDevice, VkSemaphoreWaitInfo*, ulong, VkResult> semaphores;
        static delegate* unmanaged<VkDevice, VkDescriptorSetAllocateInfo*, VkDescriptorSet*, VkResult> allocateSets;
        static delegate* unmanaged<VkDevice, uint, VkWriteDescriptorSet*, uint, VkCopyDescriptorSet*, void> updateSets;
        static delegate* unmanaged<VkDevice, VkDescriptorPoolCreateInfo*, VkAllocationCallbacks*, VkDescriptorPool*, VkResult> createDescriptorPool;
        static delegate* unmanaged<VkDevice, VkDescriptorPool, VkDescriptorPoolResetFlags, VkResult> resetDescriptorPool;
        static delegate* unmanaged<VkCommandBuffer, VkCommandBufferBeginInfo*, VkResult> beginCommandBuffer;
        static delegate* unmanaged<VkCommandBuffer, VkResult> endCommandBuffer;
        static delegate* unmanaged<VkDevice, VkCommandBufferAllocateInfo*, VkCommandBuffer*, VkResult> allocateCommandBuffers;
        static delegate* unmanaged<VkDevice, VkCommandPool, VkCommandPoolResetFlags, VkResult> resetCommandPool;
        static delegate* unmanaged<VkDevice, VkCommandPoolCreateInfo*, VkAllocationCallbacks*, VkCommandPool*, VkResult> createCommandPool;
        static delegate* unmanaged<VkDevice, VkMemoryAllocateInfo*, VkAllocationCallbacks*, VkDeviceMemory*, VkResult> allocateMemory;
        static delegate* unmanaged<VkDevice, VkBufferCreateInfo*, VkAllocationCallbacks*, VkBuffer*, VkResult> createBuffer;
        static delegate* unmanaged<VkDevice, VkDeviceMemory, ulong, ulong, VkMemoryMapFlags, void**, VkResult> mapMemory;
        static delegate* unmanaged<VkCommandBuffer, VkRenderingInfo*, void> beginRendering;
        static delegate* unmanaged<VkCommandBuffer, VkPipelineStageFlags, VkPipelineStageFlags, VkDependencyFlags, uint, VkMemoryBarrier*, uint, VkBufferMemoryBarrier*, uint, VkImageMemoryBarrier*, void> barrier;
        static delegate* unmanaged<VkCommandBuffer, VkPipelineBindPoint, VkPipelineLayout, uint, uint, VkDescriptorSet*, uint, uint*, void> bindSets;

        /// <summary>Right after the device is made, on the game thread; once.</summary>
        public static void Install(GraphicsDevice graphicsDevice)
        {
            if (submit != null)
                return;
            gameThread = Environment.CurrentManagedThreadId;
            VkDeviceApi api = StrideVulkan.DeviceApi(graphicsDevice);
            // Readonly fields Stride reads at each call, as for the pipeline cache.
            static void* Swap(in PFN_vkVoidFunction field, void* hook)
            {
                void* original = (void*)field.Value;
                Unsafe.AsRef(in field) = new PFN_vkVoidFunction((delegate* unmanaged<void>)hook);
                return original;
            }
            submit = (delegate* unmanaged<VkQueue, uint, VkSubmitInfo2*, VkFence, VkResult>)Swap(api.vkQueueSubmit2_ptr, (delegate* unmanaged<VkQueue, uint, VkSubmitInfo2*, VkFence, VkResult>)&Submit);
            present = (delegate* unmanaged<VkQueue, VkPresentInfoKHR*, VkResult>)Swap(api.vkQueuePresentKHR_ptr, (delegate* unmanaged<VkQueue, VkPresentInfoKHR*, VkResult>)&Present);
            fences = (delegate* unmanaged<VkDevice, uint, VkFence*, VkBool32, ulong, VkResult>)Swap(api.vkWaitForFences_ptr, (delegate* unmanaged<VkDevice, uint, VkFence*, VkBool32, ulong, VkResult>)&Fences);
            acquire = (delegate* unmanaged<VkDevice, VkSwapchainKHR, ulong, VkSemaphore, VkFence, uint*, VkResult>)Swap(api.vkAcquireNextImageKHR_ptr, (delegate* unmanaged<VkDevice, VkSwapchainKHR, ulong, VkSemaphore, VkFence, uint*, VkResult>)&Acquire);
            semaphores = (delegate* unmanaged<VkDevice, VkSemaphoreWaitInfo*, ulong, VkResult>)Swap(api.vkWaitSemaphores_ptr, (delegate* unmanaged<VkDevice, VkSemaphoreWaitInfo*, ulong, VkResult>)&Semaphores);
            allocateSets = (delegate* unmanaged<VkDevice, VkDescriptorSetAllocateInfo*, VkDescriptorSet*, VkResult>)Swap(api.vkAllocateDescriptorSets_ptr, (delegate* unmanaged<VkDevice, VkDescriptorSetAllocateInfo*, VkDescriptorSet*, VkResult>)&AllocateSets);
            updateSets = (delegate* unmanaged<VkDevice, uint, VkWriteDescriptorSet*, uint, VkCopyDescriptorSet*, void>)Swap(api.vkUpdateDescriptorSets_ptr, (delegate* unmanaged<VkDevice, uint, VkWriteDescriptorSet*, uint, VkCopyDescriptorSet*, void>)&UpdateSets);
            createDescriptorPool = (delegate* unmanaged<VkDevice, VkDescriptorPoolCreateInfo*, VkAllocationCallbacks*, VkDescriptorPool*, VkResult>)Swap(api.vkCreateDescriptorPool_ptr, (delegate* unmanaged<VkDevice, VkDescriptorPoolCreateInfo*, VkAllocationCallbacks*, VkDescriptorPool*, VkResult>)&CreateDescriptorPool);
            resetDescriptorPool = (delegate* unmanaged<VkDevice, VkDescriptorPool, VkDescriptorPoolResetFlags, VkResult>)Swap(api.vkResetDescriptorPool_ptr, (delegate* unmanaged<VkDevice, VkDescriptorPool, VkDescriptorPoolResetFlags, VkResult>)&ResetDescriptorPool);
            beginCommandBuffer = (delegate* unmanaged<VkCommandBuffer, VkCommandBufferBeginInfo*, VkResult>)Swap(api.vkBeginCommandBuffer_ptr, (delegate* unmanaged<VkCommandBuffer, VkCommandBufferBeginInfo*, VkResult>)&BeginCommandBuffer);
            endCommandBuffer = (delegate* unmanaged<VkCommandBuffer, VkResult>)Swap(api.vkEndCommandBuffer_ptr, (delegate* unmanaged<VkCommandBuffer, VkResult>)&EndCommandBuffer);
            allocateCommandBuffers = (delegate* unmanaged<VkDevice, VkCommandBufferAllocateInfo*, VkCommandBuffer*, VkResult>)Swap(api.vkAllocateCommandBuffers_ptr, (delegate* unmanaged<VkDevice, VkCommandBufferAllocateInfo*, VkCommandBuffer*, VkResult>)&AllocateCommandBuffers);
            resetCommandPool = (delegate* unmanaged<VkDevice, VkCommandPool, VkCommandPoolResetFlags, VkResult>)Swap(api.vkResetCommandPool_ptr, (delegate* unmanaged<VkDevice, VkCommandPool, VkCommandPoolResetFlags, VkResult>)&ResetCommandPool);
            createCommandPool = (delegate* unmanaged<VkDevice, VkCommandPoolCreateInfo*, VkAllocationCallbacks*, VkCommandPool*, VkResult>)Swap(api.vkCreateCommandPool_ptr, (delegate* unmanaged<VkDevice, VkCommandPoolCreateInfo*, VkAllocationCallbacks*, VkCommandPool*, VkResult>)&CreateCommandPool);
            allocateMemory = (delegate* unmanaged<VkDevice, VkMemoryAllocateInfo*, VkAllocationCallbacks*, VkDeviceMemory*, VkResult>)Swap(api.vkAllocateMemory_ptr, (delegate* unmanaged<VkDevice, VkMemoryAllocateInfo*, VkAllocationCallbacks*, VkDeviceMemory*, VkResult>)&AllocateMemory);
            createBuffer = (delegate* unmanaged<VkDevice, VkBufferCreateInfo*, VkAllocationCallbacks*, VkBuffer*, VkResult>)Swap(api.vkCreateBuffer_ptr, (delegate* unmanaged<VkDevice, VkBufferCreateInfo*, VkAllocationCallbacks*, VkBuffer*, VkResult>)&CreateBuffer);
            mapMemory = (delegate* unmanaged<VkDevice, VkDeviceMemory, ulong, ulong, VkMemoryMapFlags, void**, VkResult>)Swap(api.vkMapMemory_ptr, (delegate* unmanaged<VkDevice, VkDeviceMemory, ulong, ulong, VkMemoryMapFlags, void**, VkResult>)&MapMemory);
            beginRendering = (delegate* unmanaged<VkCommandBuffer, VkRenderingInfo*, void>)Swap(api.vkCmdBeginRendering_ptr, (delegate* unmanaged<VkCommandBuffer, VkRenderingInfo*, void>)&BeginRendering);
            barrier = (delegate* unmanaged<VkCommandBuffer, VkPipelineStageFlags, VkPipelineStageFlags, VkDependencyFlags, uint, VkMemoryBarrier*, uint, VkBufferMemoryBarrier*, uint, VkImageMemoryBarrier*, void>)Swap(api.vkCmdPipelineBarrier_ptr, (delegate* unmanaged<VkCommandBuffer, VkPipelineStageFlags, VkPipelineStageFlags, VkDependencyFlags, uint, VkMemoryBarrier*, uint, VkBufferMemoryBarrier*, uint, VkImageMemoryBarrier*, void>)&Barrier);
            bindSets = (delegate* unmanaged<VkCommandBuffer, VkPipelineBindPoint, VkPipelineLayout, uint, uint, VkDescriptorSet*, uint, uint*, void>)Swap(api.vkCmdBindDescriptorSets_ptr, (delegate* unmanaged<VkCommandBuffer, VkPipelineBindPoint, VkPipelineLayout, uint, uint, VkDescriptorSet*, uint, uint*, void>)&BindSets);
        }

        /// <summary>Per call, ms and count on the game thread, then ms on all other threads, since the last
        /// Take (each span Call.Count long); then cleared.</summary>
        public static void Take(Span<float> gameMs, Span<int> gameCount, Span<float> otherMs)
        {
            for (int i = 0; i < N; i++)
            {
                gameMs[i] = Ms(Interlocked.Exchange(ref ticks[i], 0));
                gameCount[i] = Interlocked.Exchange(ref calls[i], 0);
                otherMs[i] = Ms(Interlocked.Exchange(ref ticks[N + i], 0));
                Interlocked.Exchange(ref calls[N + i], 0);
            }
        }

        static float Ms(long t) => (float)(t * 1000.0 / Stopwatch.Frequency);

        static void Add(Call call, long from)
        {
            int i = (int)call + (Environment.CurrentManagedThreadId == gameThread ? 0 : N);
            Interlocked.Add(ref ticks[i], Stopwatch.GetTimestamp() - from);
            Interlocked.Increment(ref calls[i]);
        }

        static long Now => Stopwatch.GetTimestamp();

        [UnmanagedCallersOnly] static VkResult Submit(VkQueue q, uint n, VkSubmitInfo2* i, VkFence f) { long at = Now; VkResult r = submit(q, n, i, f); Add(Call.Submit, at); return r; }
        [UnmanagedCallersOnly] static VkResult Present(VkQueue q, VkPresentInfoKHR* i) { long at = Now; VkResult r = present(q, i); Add(Call.Present, at); return r; }
        [UnmanagedCallersOnly] static VkResult Fences(VkDevice d, uint n, VkFence* f, VkBool32 all, ulong t) { long at = Now; VkResult r = fences(d, n, f, all, t); Add(Call.FrameFence, at); return r; }
        [UnmanagedCallersOnly] static VkResult Acquire(VkDevice d, VkSwapchainKHR s, ulong t, VkSemaphore sem, VkFence f, uint* index) { long at = Now; VkResult r = acquire(d, s, t, sem, f, index); Add(Call.Acquire, at); return r; }
        [UnmanagedCallersOnly] static VkResult Semaphores(VkDevice d, VkSemaphoreWaitInfo* i, ulong t) { long at = Now; VkResult r = semaphores(d, i, t); Add(Call.WaitSemaphores, at); return r; }
        [UnmanagedCallersOnly] static VkResult AllocateSets(VkDevice d, VkDescriptorSetAllocateInfo* i, VkDescriptorSet* s) { long at = Now; VkResult r = allocateSets(d, i, s); Add(Call.AllocateSets, at); return r; }
        [UnmanagedCallersOnly] static void UpdateSets(VkDevice d, uint n, VkWriteDescriptorSet* w, uint m, VkCopyDescriptorSet* c) { long at = Now; updateSets(d, n, w, m, c); Add(Call.UpdateSets, at); }
        [UnmanagedCallersOnly] static VkResult CreateDescriptorPool(VkDevice d, VkDescriptorPoolCreateInfo* i, VkAllocationCallbacks* a, VkDescriptorPool* p) { long at = Now; VkResult r = createDescriptorPool(d, i, a, p); Add(Call.CreateDescriptorPool, at); return r; }
        [UnmanagedCallersOnly] static VkResult ResetDescriptorPool(VkDevice d, VkDescriptorPool p, VkDescriptorPoolResetFlags f) { long at = Now; VkResult r = resetDescriptorPool(d, p, f); Add(Call.ResetDescriptorPool, at); return r; }
        [UnmanagedCallersOnly] static VkResult BeginCommandBuffer(VkCommandBuffer b, VkCommandBufferBeginInfo* i) { long at = Now; VkResult r = beginCommandBuffer(b, i); Add(Call.BeginCommandBuffer, at); return r; }
        [UnmanagedCallersOnly] static VkResult EndCommandBuffer(VkCommandBuffer b) { long at = Now; VkResult r = endCommandBuffer(b); Add(Call.EndCommandBuffer, at); return r; }
        [UnmanagedCallersOnly] static VkResult AllocateCommandBuffers(VkDevice d, VkCommandBufferAllocateInfo* i, VkCommandBuffer* b) { long at = Now; VkResult r = allocateCommandBuffers(d, i, b); Add(Call.AllocateCommandBuffers, at); return r; }
        [UnmanagedCallersOnly] static VkResult ResetCommandPool(VkDevice d, VkCommandPool p, VkCommandPoolResetFlags f) { long at = Now; VkResult r = resetCommandPool(d, p, f); Add(Call.ResetCommandPool, at); return r; }
        [UnmanagedCallersOnly] static VkResult CreateCommandPool(VkDevice d, VkCommandPoolCreateInfo* i, VkAllocationCallbacks* a, VkCommandPool* p) { long at = Now; VkResult r = createCommandPool(d, i, a, p); Add(Call.CreateCommandPool, at); return r; }
        [UnmanagedCallersOnly] static VkResult AllocateMemory(VkDevice d, VkMemoryAllocateInfo* i, VkAllocationCallbacks* a, VkDeviceMemory* m) { long at = Now; VkResult r = allocateMemory(d, i, a, m); Add(Call.AllocateMemory, at); return r; }
        [UnmanagedCallersOnly] static VkResult CreateBuffer(VkDevice d, VkBufferCreateInfo* i, VkAllocationCallbacks* a, VkBuffer* b) { long at = Now; VkResult r = createBuffer(d, i, a, b); Add(Call.CreateBuffer, at); return r; }
        [UnmanagedCallersOnly] static VkResult MapMemory(VkDevice d, VkDeviceMemory m, ulong o, ulong s, VkMemoryMapFlags f, void** p) { long at = Now; VkResult r = mapMemory(d, m, o, s, f, p); Add(Call.MapMemory, at); return r; }
        [UnmanagedCallersOnly] static void BeginRendering(VkCommandBuffer b, VkRenderingInfo* i) { long at = Now; beginRendering(b, i); Add(Call.BeginRendering, at); }
        [UnmanagedCallersOnly] static void Barrier(VkCommandBuffer b, VkPipelineStageFlags s, VkPipelineStageFlags t, VkDependencyFlags f, uint n, VkMemoryBarrier* m, uint bn, VkBufferMemoryBarrier* bb, uint ni, VkImageMemoryBarrier* ib) { long at = Now; barrier(b, s, t, f, n, m, bn, bb, ni, ib); Add(Call.PipelineBarrier, at); }
        [UnmanagedCallersOnly] static void BindSets(VkCommandBuffer b, VkPipelineBindPoint p, VkPipelineLayout l, uint first, uint n, VkDescriptorSet* s, uint dn, uint* d) { long at = Now; bindSets(b, p, l, first, n, s, dn, d); Add(Call.BindSets, at); }
    }
}
