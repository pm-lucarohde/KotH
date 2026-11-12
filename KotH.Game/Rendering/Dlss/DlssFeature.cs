using System;
using Stride.Graphics;
using Vortice.Vulkan;

namespace KotH.Client.Rendering.Dlss
{
    /// <summary>One DLSS Super Resolution instance for a render size, output size and mode, made on
    /// first use inside a frame's command buffer; Evaluate upscales one frame. Inputs: HDR colour,
    /// depth and motion vectors at render size (in UV, see DlssMotion), exposure fixed at 1.</summary>
    internal sealed unsafe class DlssFeature : IDisposable
    {
        readonly GraphicsDevice graphics;
        void* parameters;
        void* handle;
        Texture exposure;

        public int RenderWidth { get; private set; }
        public int RenderHeight { get; private set; }
        public int OutputWidth { get; private set; }
        public int OutputHeight { get; private set; }
        public DlssMode Mode { get; private set; }

        public DlssFeature(GraphicsDevice graphics) => this.graphics = graphics;

        /// <summary>Whether it fits these sizes and mode; else Dispose and make a new one.</summary>
        public bool Fits(int renderWidth, int renderHeight, int outputWidth, int outputHeight, DlssMode mode)
            => handle != null && RenderWidth == renderWidth && RenderHeight == renderHeight
               && OutputWidth == outputWidth && OutputHeight == outputHeight && Mode == mode;

        public bool Create(CommandList list, int renderWidth, int renderHeight, int outputWidth, int outputHeight, DlssMode mode)
        {
            Release();
            void* p = parameters;
            if (p == null && Ngx.Failed(Ngx.NVSDK_NGX_VULKAN_AllocateParameters(&p)))
                return false;
            parameters = p;
            Ngx.SetUI(p, Ngx.CreationNodeMask, 1);
            Ngx.SetUI(p, Ngx.VisibilityNodeMask, 1);
            Ngx.SetUI(p, Ngx.Width, (uint)renderWidth);
            Ngx.SetUI(p, Ngx.Height, (uint)renderHeight);
            Ngx.SetUI(p, Ngx.OutWidth, (uint)outputWidth);
            Ngx.SetUI(p, Ngx.OutHeight, (uint)outputHeight);
            Ngx.SetI(p, Ngx.PerfQualityValue, (int)Dlss.QualityOf(mode));
            Ngx.SetI(p, Ngx.CreateFlagsKey, (int)(Ngx.CreateFlags.IsHdr | Ngx.CreateFlags.MvLowRes));
            Ngx.SetI(p, Ngx.EnableOutputSubrects, 0);
            uint preset = Dlss.PresetOf(mode);
            Ngx.SetUI(p, Ngx.PresetDlaa, preset);
            Ngx.SetUI(p, Ngx.PresetQuality, preset);
            Ngx.SetUI(p, Ngx.PresetBalanced, preset);
            Ngx.SetUI(p, Ngx.PresetPerformance, preset);
            Ngx.SetUI(p, Ngx.PresetUltraPerformance, preset);

            StrideVulkan.BeforeForeign(list);
            void* h = null;
            int result = Ngx.NVSDK_NGX_VULKAN_CreateFeature1(StrideVulkan.Device(graphics), StrideVulkan.CommandBuffer(list),
                                                             Ngx.FeatureSuperSampling, p, &h);
            StrideVulkan.AfterForeign(list);
            if (Ngx.Failed(result))
            {
                Console.WriteLine($"[dlss] feature not made (0x{result:X8})");
                return false;
            }
            handle = h;
            (RenderWidth, RenderHeight, OutputWidth, OutputHeight, Mode) = (renderWidth, renderHeight, outputWidth, outputHeight, mode);
            exposure ??= Texture.New2D(graphics, 1, 1, PixelFormat.R32_Float, new[] { 1f });
            Console.WriteLine($"[dlss] {mode} {renderWidth}x{renderHeight} -> {outputWidth}x{outputHeight}, preset {(char)('A' + preset - 1)}");
            return true;
        }

        /// <summary>Upscales colour into output (made with UnorderedAccess); jitter in render pixels.</summary>
        public bool Evaluate(CommandList list, Texture color, Texture depth, Texture motion, Texture output,
                             float jitterX, float jitterY, bool reset, float frameMs)
        {
            if (handle == null)
                return false;
            list.ResourceBarrierTransition(color, BarrierLayout.ShaderResource);
            list.ResourceBarrierTransition(depth, BarrierLayout.ShaderResource);
            list.ResourceBarrierTransition(motion, BarrierLayout.ShaderResource);
            list.ResourceBarrierTransition(exposure, BarrierLayout.ShaderResource);
            list.ResourceBarrierTransition(output, BarrierLayout.UnorderedAccess);
            StrideVulkan.BeforeForeign(list);
            VkCommandBuffer buffer = StrideVulkan.CommandBuffer(list);
            FullBarrier(buffer);

            Ngx.ResourceVk inColor = Resource(color, false), inDepth = Resource(depth, false), inMotion = Resource(motion, false),
                           inExposure = Resource(exposure, false), outColor = Resource(output, true);
            void* p = parameters;
            Ngx.SetPointer(p, Ngx.Color, &inColor);
            Ngx.SetPointer(p, Ngx.Output, &outColor);
            Ngx.SetPointer(p, Ngx.Depth, &inDepth);
            Ngx.SetPointer(p, Ngx.MotionVectors, &inMotion);
            Ngx.SetPointer(p, Ngx.ExposureTextureKey, &inExposure);
            Ngx.SetF(p, Ngx.JitterX, jitterX);
            Ngx.SetF(p, Ngx.JitterY, jitterY);
            Ngx.SetI(p, Ngx.Reset, reset ? 1 : 0);
            Ngx.SetF(p, Ngx.MvScaleX, RenderWidth);
            Ngx.SetF(p, Ngx.MvScaleY, RenderHeight);
            Ngx.SetUI(p, Ngx.RenderWidth, (uint)RenderWidth);
            Ngx.SetUI(p, Ngx.RenderHeight, (uint)RenderHeight);
            Ngx.SetF(p, Ngx.PreExposure, 1f);
            Ngx.SetF(p, Ngx.ExposureScale, 1f);
            Ngx.SetF(p, Ngx.FrameTime, frameMs);
            int result = Ngx.NVSDK_NGX_VULKAN_EvaluateFeature_C(buffer, handle, p, null);

            FullBarrier(buffer);
            StrideVulkan.AfterForeign(list);
            if (Ngx.Failed(result))
            {
                Console.WriteLine($"[dlss] evaluate failed (0x{result:X8})");
                return false;
            }
            return true;
        }

        // NGX works in compute; Stride's barriers name only the graphics stages.
        void FullBarrier(VkCommandBuffer buffer)
        {
            var barrier = new VkMemoryBarrier
            {
                sType = VkStructureType.MemoryBarrier,
                srcAccessMask = VkAccessFlags.MemoryWrite,
                dstAccessMask = VkAccessFlags.MemoryRead | VkAccessFlags.MemoryWrite,
            };
            StrideVulkan.DeviceApi(graphics).vkCmdPipelineBarrier(buffer, VkPipelineStageFlags.AllCommands, VkPipelineStageFlags.AllCommands,
                0, 1, &barrier, 0, null, 0, null);
        }

        static Ngx.ResourceVk Resource(Texture texture, bool write) => new()
        {
            ImageView = StrideVulkan.ImageView(texture),
            Image = StrideVulkan.Image(texture),
            SubresourceRange = Range(texture),
            Format = StrideVulkan.Format(texture),
            Width = (uint)texture.Width,
            Height = (uint)texture.Height,
            Type = 0,
            ReadWrite = write ? (byte)1 : (byte)0,
        };

        // One mip, one layer; depth formats by their depth aspect alone.
        static VkImageSubresourceRange Range(Texture texture)
        {
            VkImageSubresourceRange range = StrideVulkan.Range(texture);
            range.levelCount = 1;
            range.layerCount = 1;
            if (texture.IsDepthStencil || (range.aspectMask & VkImageAspectFlags.Depth) != 0)
                range.aspectMask = VkImageAspectFlags.Depth;
            return range;
        }

        void Release()
        {
            if (handle != null)
                Ngx.NVSDK_NGX_VULKAN_ReleaseFeature(handle);
            handle = null;
        }

        public void Dispose()
        {
            Release();
            if (parameters != null)
                Ngx.NVSDK_NGX_VULKAN_DestroyParameters(parameters);
            parameters = null;
            exposure?.Dispose();
            exposure = null;
        }
    }
}
