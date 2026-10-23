// KotH patch: the textures of VoxelStorageSparse, see there.
using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.ComputeEffect;
using Stride.Shaders;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels
{
    public class VoxelStorageTextureSparse : IVoxelStorageTexture
    {
        /// <summary>Bricks per atlas row and layer; the atlas grows in z.</summary>
        public const int AtlasBricksXY = 64;

        public float VoxelSize;
        public Vector3 VolumeMin;
        public Int3 CellCount;
        public Int3 AtlasBricks;
        public int BrickCount;
        public PixelFormat Format;

        public Texture Pages;
        public Buffer SlotCells;
        /// <summary>Levels 0-2 (voxel size, x2, x4), bricks of 9, 5 and 3 texels with their +1 border.</summary>
        public Texture[] Atlases = new Texture[3];
        public Texture Dense;
        Texture[] denseTemp;

        /// <summary>Set by the storage once every brick is written: the next PostProcess builds the coarser levels.</summary>
        public bool BuildLevels;
        public bool Ready;

        Texture emptyPages;
        Texture emptyVoxels;

        /// <summary>What sampling reads until the bake is done: nothing.</summary>
        public void CreateEmpty(GraphicsDevice device)
        {
            emptyPages ??= Texture.New3D(device, 1, 1, 1, PixelFormat.R32_UInt, new uint[] { 0 });
            emptyVoxels ??= Texture.New3D(device, 1, 1, 1, PixelFormat.R8G8B8A8_UNorm, new uint[] { 0 });
        }

        /// <summary>The atlases for <see cref="BrickCount"/> bricks and <paramref name="extraBricks"/> more, cleared.</summary>
        public void CreateAtlases(GraphicsDevice device, CommandList commandList, int extraBricks)
        {
            int layers = (BrickCount + extraBricks + AtlasBricksXY * AtlasBricksXY - 1) / (AtlasBricksXY * AtlasBricksXY);
            AtlasBricks = new Int3(AtlasBricksXY, AtlasBricksXY, Math.Max(1, layers));
            for (int level = 0; level < 3; level++)
            {
                int side = (8 >> level) + 1;
                Atlases[level]?.Dispose();
                Atlases[level] = Texture.New3D(device, AtlasBricks.X * side, AtlasBricks.Y * side, AtlasBricks.Z * side, false, Format,
                    TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
                commandList.ClearReadWrite(Atlases[level], Vector4.Zero);
            }
            Dense?.Dispose();
            Dense = Texture.New3D(device, CellCount.X, CellCount.Y, CellCount.Z, MipMapCount.Auto, Format,
                TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            if (denseTemp != null)
                foreach (var texture in denseTemp)
                    texture.Dispose();
            denseTemp = new Texture[Dense.MipLevelCount - 1];
            for (int mip = 1; mip < Dense.MipLevelCount; mip++)
                denseTemp[mip - 1] = Texture.New3D(device, Math.Max(1, CellCount.X >> mip), Math.Max(1, CellCount.Y >> mip), Math.Max(1, CellCount.Z >> mip),
                    false, Format, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        }

        public void UpdateVoxelizationLayout(string compositionName)
        {
        }

        public void ApplyVoxelizationParameters(ObjectParameterKey<Texture> MainKey, ParameterCollection parameters)
        {
            parameters.Set(MainKey, Atlases[0]);
        }

        ComputeEffectShader[] levelShaders;
        ComputeEffectShader denseShader;
        ComputeEffectShader[] denseMipShaders;

        /// <summary>The attribute's mipmapper, kept for the frames that only redo the moving groups.</summary>
        ShaderSource mipmapper;

        public void PostProcess(RenderDrawContext drawContext, ShaderSource[] mipmapShaders)
        {
            if (!BuildLevels || mipmapShaders.Length != 1)
                return;
            BuildLevels = false;
            mipmapper = mipmapShaders[0];
            BuildBrickLevels(drawContext, 0, BrickCount);
            BuildDense(drawContext, Int3.Zero, CellCount);

            // The dense mipmaps go through textures of their own: one texture
            // cannot be read and written at once, even at different mipmaps.
            if (denseMipShaders == null || denseMipShaders.Length != denseTemp.Length)
            {
                denseMipShaders = new ComputeEffectShader[denseTemp.Length];
                for (int i = 0; i < denseMipShaders.Length; i++)
                    denseMipShaders[i] = new ComputeEffectShader(drawContext.RenderContext) { ShaderSourceName = "Voxel2x2x2MipmapEffect" };
            }
            for (int i = 0; i < denseTemp.Length; i++)
            {
                var shader = denseMipShaders[i];
                var size = new Int3(denseTemp[i].Width, denseTemp[i].Height, denseTemp[i].Depth);
                var threads = Int3.Min(size, new Int3(8));
                shader.ThreadNumbers = threads;
                shader.ThreadGroupCounts = new Int3((size.X + threads.X - 1) / threads.X, (size.Y + threads.Y - 1) / threads.Y, (size.Z + threads.Z - 1) / threads.Z);
                shader.Parameters.Set(Voxel2x2x2MipmapKeys.ReadTex, i == 0 ? Dense : denseTemp[i - 1]);
                shader.Parameters.Set(Voxel2x2x2MipmapKeys.WriteTex, denseTemp[i]);
                shader.Parameters.Set(Voxel2x2x2MipmapKeys.ReadOffset, Vector3.Zero);
                shader.Parameters.Set(Voxel2x2x2MipmapKeys.WriteOffset, Vector3.Zero);
                shader.Parameters.Set(Voxel2x2x2MipmapKeys.mipmapper, mipmapper);
                ((RendererBase)shader).Draw(drawContext);
                drawContext.CommandList.CopyRegion(denseTemp[i], 0, null, Dense, i + 1);
            }
            Ready = true;
        }

        /// <summary>The bricks at x2 and x4 of <paramref name="count"/> slots, each from the level below.</summary>
        public void BuildBrickLevels(RenderDrawContext drawContext, int slotStart, int count)
        {
            levelShaders ??= new[]
            {
                new ComputeEffectShader(drawContext.RenderContext) { ShaderSourceName = "SparseMipEffect" },
                new ComputeEffectShader(drawContext.RenderContext) { ShaderSourceName = "SparseMipEffect" },
            };
            for (int level = 1; level < 3; level++)
            {
                var shader = levelShaders[level - 1];
                int side = (8 >> level) + 1;
                SetLevelParameters(shader, Atlases[level - 1], Atlases[level], 8 >> (level - 1));
                shader.Parameters.Set(SparseMipKeys.Dense, 0u);
                shader.Parameters.Set(SparseMipKeys.slotStart, (uint)slotStart);
                shader.Parameters.Set(SparseMipKeys.brickCount, (uint)count);
                shader.ThreadNumbers = new Int3(256, 1, 1);
                shader.ThreadGroupCounts = VoxelStorageSparse.LinearDispatch(count * side * side * side, 256, out int rowLength);
                shader.Parameters.Set(SparseMipKeys.rowLength, (uint)rowLength);
                ((RendererBase)shader).Draw(drawContext);
            }
        }

        /// <summary>The dense volume (one voxel per cell) in a box of cells, from the bricks at x4.</summary>
        public void BuildDense(RenderDrawContext drawContext, Int3 offset, Int3 size)
        {
            denseShader ??= new ComputeEffectShader(drawContext.RenderContext) { ShaderSourceName = "SparseMipEffect" };
            SetLevelParameters(denseShader, Atlases[2], Dense, 2);
            denseShader.Parameters.Set(SparseMipKeys.Dense, 1u);
            denseShader.Parameters.Set(SparseMipKeys.cellOffset, offset);
            denseShader.ThreadNumbers = new Int3(8);
            denseShader.ThreadGroupCounts = (size + new Int3(7)) / 8;
            ((RendererBase)denseShader).Draw(drawContext);
        }

        void SetLevelParameters(ComputeEffectShader shader, Texture read, Texture write, int readSide)
        {
            shader.Parameters.Set(Voxel2x2x2MipmapKeys.mipmapper, mipmapper);
            shader.Parameters.Set(SparseMipKeys.Pages, Pages);
            shader.Parameters.Set(SparseMipKeys.SlotCells, SlotCells);
            shader.Parameters.Set(SparseMipKeys.ReadAtlas, read);
            shader.Parameters.Set(SparseMipKeys.WriteAtlas, write);
            shader.Parameters.Set(SparseMipKeys.atlasBricks, AtlasBricks);
            shader.Parameters.Set(SparseMipKeys.cellCount, CellCount);
            shader.Parameters.Set(SparseMipKeys.readSide, (uint)readSide);
        }

        ObjectParameterKey<Texture> pagesKey;
        ObjectParameterKey<Texture>[] atlasKeys;
        ObjectParameterKey<Texture> denseKey;
        ValueParameterKey<Vector3> volumeMinKey;
        ValueParameterKey<Vector3> cellCountKey;
        ValueParameterKey<Vector3> atlasBricksKey;
        ValueParameterKey<Vector3>[] atlasSizeKeys;

        public void UpdateSamplingLayout(string compositionName)
        {
            pagesKey = VoxelStorageTextureSparseShaderKeys.pages.ComposeWith(compositionName);
            atlasKeys = new[]
            {
                VoxelStorageTextureSparseShaderKeys.atlas0.ComposeWith(compositionName),
                VoxelStorageTextureSparseShaderKeys.atlas1.ComposeWith(compositionName),
                VoxelStorageTextureSparseShaderKeys.atlas2.ComposeWith(compositionName),
            };
            denseKey = VoxelStorageTextureSparseShaderKeys.dense.ComposeWith(compositionName);
            volumeMinKey = VoxelStorageTextureSparseShaderKeys.volumeMin.ComposeWith(compositionName);
            cellCountKey = VoxelStorageTextureSparseShaderKeys.cellCount.ComposeWith(compositionName);
            atlasBricksKey = VoxelStorageTextureSparseShaderKeys.atlasBricks.ComposeWith(compositionName);
            atlasSizeKeys = new[]
            {
                VoxelStorageTextureSparseShaderKeys.atlasSize0.ComposeWith(compositionName),
                VoxelStorageTextureSparseShaderKeys.atlasSize1.ComposeWith(compositionName),
                VoxelStorageTextureSparseShaderKeys.atlasSize2.ComposeWith(compositionName),
            };
        }

        public ShaderClassSource GetSamplingShader() => new ShaderClassSource("VoxelStorageTextureSparseShader", VoxelSize);

        public void ApplySamplingParameters(VoxelViewContext viewContext, ParameterCollection parameters)
        {
            // Until the bake is done every brick reads as empty.
            parameters.Set(pagesKey, Ready ? Pages : emptyPages);
            for (int level = 0; level < 3; level++)
            {
                parameters.Set(atlasKeys[level], Ready ? Atlases[level] : emptyVoxels);
                int side = (8 >> level) + 1;
                parameters.Set(atlasSizeKeys[level], new Vector3(AtlasBricks.X * side, AtlasBricks.Y * side, AtlasBricks.Z * side));
            }
            parameters.Set(denseKey, Ready ? Dense : emptyVoxels);
            parameters.Set(volumeMinKey, VolumeMin);
            parameters.Set(cellCountKey, new Vector3(CellCount.X, CellCount.Y, CellCount.Z));
            parameters.Set(atlasBricksKey, new Vector3(AtlasBricks.X, AtlasBricks.Y, AtlasBricks.Z));
        }
    }
}
