// KotH patch: the storer of VoxelStorageSparse, see there.
using Stride.Core.Mathematics;
using Stride.Shaders;
using Buffer = Stride.Graphics.Buffer;
using Texture = Stride.Graphics.Texture;

namespace Stride.Rendering.Voxels
{
    public class VoxelStorerSparse : IVoxelStorer
    {
        /// <summary>Only marks the cells hit; otherwise writes the fragments of one slab.</summary>
        public bool Mark;
        public int SlabStart;
        public int SlabCount;
        public int StorageUints;
        public Vector3 Resolution;
        public Vector3 VolumeScale;
        public Vector3 VolumeOffset;
        /// <summary>World to the rasterised box, [0, 1]: the volume, or a box round a moving group.</summary>
        public Vector3 ViewScale;
        public Vector3 ViewOffset;
        public Buffer FragmentsBuffer;
        public Texture Pages;
        public Buffer Marks;

        public bool CanShareRenderStage(IVoxelStorer storer) => storer is VoxelStorerSparse;

        public override bool Equals(object obj) =>
            obj is VoxelStorerSparse other && other.Mark == Mark && other.SlabStart == SlabStart && other.ViewOffset == ViewOffset;

        public override int GetHashCode() => (Mark, SlabStart, ViewOffset).GetHashCode();

        ObjectParameterKey<Buffer> fragmentsBufferKey;
        ObjectParameterKey<Buffer> marksKey;
        ObjectParameterKey<Texture> pagesKey;
        ValueParameterKey<Vector3> resolutionKey;
        ValueParameterKey<Vector3> volumeScaleKey;
        ValueParameterKey<Vector3> volumeOffsetKey;
        ValueParameterKey<Vector3> viewScaleKey;
        ValueParameterKey<Vector3> viewOffsetKey;
        ValueParameterKey<float> storageUintsKey;
        ValueParameterKey<float> slabStartKey;
        ValueParameterKey<float> slabCountKey;

        public void UpdateVoxelizationLayout(string compositionName)
        {
            fragmentsBufferKey = VoxelStorageSparseShaderKeys.fragmentsBuffer.ComposeWith(compositionName);
            marksKey = VoxelStorageSparseShaderKeys.marks.ComposeWith(compositionName);
            pagesKey = VoxelStorageSparseShaderKeys.pages.ComposeWith(compositionName);
            resolutionKey = VoxelStorageSparseShaderKeys.volumeResolution.ComposeWith(compositionName);
            volumeScaleKey = VoxelStorageSparseShaderKeys.volumeScale.ComposeWith(compositionName);
            volumeOffsetKey = VoxelStorageSparseShaderKeys.volumeOffset.ComposeWith(compositionName);
            viewScaleKey = VoxelStorageSparseShaderKeys.viewScale.ComposeWith(compositionName);
            viewOffsetKey = VoxelStorageSparseShaderKeys.viewOffset.ComposeWith(compositionName);
            storageUintsKey = VoxelStorageSparseShaderKeys.storageUints.ComposeWith(compositionName);
            slabStartKey = VoxelStorageSparseShaderKeys.slabStart.ComposeWith(compositionName);
            slabCountKey = VoxelStorageSparseShaderKeys.slabCount.ComposeWith(compositionName);
        }

        public void ApplyVoxelizationParameters(ParameterCollection param)
        {
            if (Mark)
                param.Set(marksKey, Marks);
            else
            {
                param.Set(pagesKey, Pages);
                param.Set(fragmentsBufferKey, FragmentsBuffer);
            }
            param.Set(resolutionKey, Resolution);
            param.Set(volumeScaleKey, VolumeScale);
            param.Set(volumeOffsetKey, VolumeOffset);
            param.Set(viewScaleKey, ViewScale);
            param.Set(viewOffsetKey, ViewOffset);
            param.Set(storageUintsKey, StorageUints);
            param.Set(slabStartKey, SlabStart);
            param.Set(slabCountKey, SlabCount);
        }

        readonly ShaderClassSource storage = new ShaderClassSource("VoxelStorageSparseShader");

        public ShaderSource GetVoxelizationShader(VoxelizationPass pass, ProcessedVoxelVolume data)
        {
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(storage);
            mixin.AddComposition("method", pass.method.GetVoxelizationShader());
            if (Mark)
                mixin.AddMacro("SparseMark", true);

            string IndirectStoreMacro = "";
            for (int i = 0; i < pass.AttributesIndirect.Count; i++)
                IndirectStoreMacro += $"AttributesIndirect[{i}].IndirectWrite(fragmentsBuffer, writeindex + {pass.AttributesIndirect[i].BufferOffset});\n";
            mixin.AddMacro("IndirectStoreMacro", IndirectStoreMacro);

            foreach (var attr in pass.AttributesTemp)
                mixin.AddCompositionToArray("AttributesTemp", attr.GetVoxelizationShader());
            foreach (var attr in pass.AttributesDirect)
                mixin.AddCompositionToArray("AttributesDirect", attr.GetVoxelizationShader());
            foreach (var attr in pass.AttributesIndirect)
                mixin.AddCompositionToArray("AttributesIndirect", attr.GetVoxelizationShader());
            return mixin;
        }

        public bool RequireGeometryShader() => false;

        public int GeometryShaderOutputCount() => 1;

        public void PostProcess(VoxelStorageContext context, RenderDrawContext drawContext, ProcessedVoxelVolume data)
        {
        }
    }
}
