using System.Runtime.CompilerServices;
using Stride.Graphics;
using Vortice.Vulkan;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>Stride's Vulkan objects behind its public types (internal in Stride 4.4, read through
    /// UnsafeAccessor): the instance, the device, a texture's image and view, the open command buffer.</summary>
    internal static class StrideVulkan
    {
        const string Factory = "Stride.Graphics.GraphicsAdapterFactory, Stride.Graphics";
        const string FactoryInstance = "Stride.Graphics.GraphicsAdapterFactoryInstance, Stride.Graphics";

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GetInstance")]
        [return: UnsafeAccessorType(FactoryInstance)]
        public static extern object FactoryOf([UnsafeAccessorType(Factory)] object factory, bool enableValidation);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeInstanceApi")]
        public static extern ref VkInstanceApi InstanceApi([UnsafeAccessorType(FactoryInstance)] object factory);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeInstance")]
        public static extern ref VkInstance Instance([UnsafeAccessorType(FactoryInstance)] object factory);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NativePhysicalDevice")]
        public static extern VkPhysicalDevice PhysicalDevice(GraphicsDevice device);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NativeDevice")]
        public static extern VkDevice Device(GraphicsDevice device);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NativeDeviceApi")]
        public static extern VkDeviceApi DeviceApi(GraphicsDevice device);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeImage")]
        public static extern ref VkImage Image(Texture texture);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeImageView")]
        public static extern ref VkImageView ImageView(Texture texture);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeResourceRange")]
        public static extern ref VkImageSubresourceRange Range(Texture texture);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeFormat")]
        public static extern ref VkFormat Format(Texture texture);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentCommandList")]
        static extern ref CompiledCommandList Current(CommandList list);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "NativeCommandBuffer")]
        static extern ref VkCommandBuffer Buffer(ref CompiledCommandList list);

        /// <summary>The command buffer the list records into now.</summary>
        public static VkCommandBuffer CommandBuffer(CommandList list) => Buffer(ref Current(list));

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CleanupRenderPass")]
        static extern void EndRendering(CommandList list);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pipelineDirty")]
        static extern ref bool PipelineDirty(CommandList list);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "renderingDirty")]
        static extern ref bool RenderingDirty(CommandList list);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "viewportDirty")]
        static extern ref bool ViewportDirty(CommandList list);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "scissorsDirty")]
        static extern ref bool ScissorsDirty(CommandList list);

        /// <summary>Before foreign commands: no dynamic rendering may be open.</summary>
        public static void BeforeForeign(CommandList list) => EndRendering(list);

        /// <summary>After them: Stride binds pipeline, viewport and scissor again at its next draw.</summary>
        public static void AfterForeign(CommandList list)
        {
            PipelineDirty(list) = true;
            RenderingDirty(list) = true;
            ViewportDirty(list) = true;
            ScissorsDirty(list) = true;
        }
    }
}
