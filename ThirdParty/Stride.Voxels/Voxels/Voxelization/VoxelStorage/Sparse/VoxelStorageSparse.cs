// KotH patch: sparse storage, see README "Sparse storage".
using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.ComputeEffect;
using Stride.Shaders;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels
{
    /// <summary>
    /// The whole volume at the finest voxel size, kept only where there is
    /// something: bricks of 8^3 voxels, one per cell the geometry touches, found
    /// through a page per cell. Baked over several frames (effects compile
    /// asynchronously, so each step repeats until its count holds): mark the cells
    /// hit, give each a slot, write the fragments slab by slab (the typed fragment
    /// buffer is capped at 2^27), then the coarser levels. Afterwards the moving
    /// groups each frame: the cells round them read bricks of their own, copies of
    /// the baked ones the groups are written over (see SparseEdit).
    /// </summary>
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("Sparse Bricks")]
    public class VoxelStorageSparse : IVoxelStorage
    {
        enum Phase { Mark, Fill, Levels, Done }

        const int MaxBricks = 1 << 20;
        const int MaxFragments = 1 << 27;
        /// <summary>Bricks for the moving groups, after the baked ones.</summary>
        const int DynamicBricks = 1024;
        /// <summary>
        /// Voxels round a moving group's bounds: also at 0.5 m its voxels stay off the
        /// edge of its cells, so no baked brick's border or neighbour needs them.
        /// </summary>
        const int DynamicMargin = 4;
        /// <summary>
        /// Voxels across the box each moving group is rasterised in. Its own small view:
        /// the cost of a view grows with its size even where nothing is drawn.
        /// </summary>
        const int DynamicView = 64;

        /// <summary>Every brick written and the coarser levels built.</summary>
        [DataMemberIgnore]
        public bool Baked => phase == Phase.Done;

        Phase phase;
        int slab;
        int slabBricks;
        int brickCount;

        Int3 resolution;
        Int3 cellCount;
        Vector3 volumeMin;
        Vector3 volumeScale;
        float voxelSize;

        int storageUints;
        int tempStorageCounter;
        Buffer fragmentsBuffer;
        Texture pages;
        Buffer marksBuffer;
        Buffer slotCells;
        Buffer counter;
        readonly CountReader counts = new CountReader();
        VoxelStorageTextureSparse output;

        Texture staticPages;
        Buffer dynamicFragments;
        Buffer edits;
        uint[] editData;
        int editCount;
        readonly List<Int3> dynamicCells = new List<Int3>();
        readonly List<Int3> lastCells = new List<Int3>();
        readonly HashSet<Int3> cellSet = new HashSet<Int3>();
        readonly List<(Int3 Min, Int3 Size)> boxes = new List<(Int3, Int3)>();
        readonly List<Vector3> views = new List<Vector3>();
        readonly List<(Int3 Min, Int3 Size)> lastBoxes = new List<(Int3, Int3)>();

        public void UpdateFromContext(VoxelStorageContext context)
        {
            resolution = context.Resolution();
            cellCount = new Int3(resolution.X / 8, resolution.Y / 8, resolution.Z / 8);
            voxelSize = context.RealVoxelSize();
            volumeMin = context.Translation - context.Extents / 2;
            volumeScale = new Vector3(1f / context.Extents.X, 1f / context.Extents.Y, 1f / context.Extents.Z);

            if (pages == null || pages.Width != cellCount.X || pages.Height != cellCount.Y || pages.Depth != cellCount.Z)
            {
                pages?.Dispose();
                pages = Texture.New3D(context.device, cellCount.X, cellCount.Y, cellCount.Z, false, PixelFormat.R32_UInt,
                    TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
                marksBuffer?.Dispose();
                marksBuffer = Buffer.Typed.New(context.device, cellCount.X * cellCount.Y * cellCount.Z, PixelFormat.R32_UInt, true);
                slotCells ??= Buffer.Typed.New(context.device, MaxBricks, PixelFormat.R32_UInt, true);
                counter ??= Buffer.Typed.New(context.device, 4, PixelFormat.R32_UInt, true);
                counts.Create(context.device, counter);
                phase = Phase.Mark;
                pagesCleared = false;
            }
        }

        public int RequestTempStorage(int count)
        {
            int bufferOffset = tempStorageCounter / 32;
            tempStorageCounter += ((count + 31) / 32) * 32;
            return bufferOffset;
        }

        public void UpdateTempStorage(VoxelStorageContext context)
        {
            storageUints = (tempStorageCounter + 31) / 32;
            tempStorageCounter = 0;
        }

        public void UpdateTexture(VoxelStorageContext context, ref IVoxelStorageTexture texture, PixelFormat pixelFormat, int layoutSize)
        {
            if (layoutSize != 1)
                throw new NotSupportedException("Sparse bricks hold isotropic voxels only.");
            if (texture is not VoxelStorageTextureSparse sparse)
                sparse = new VoxelStorageTextureSparse();
            sparse.CreateEmpty(context.device);
            sparse.Format = pixelFormat;
            sparse.VoxelSize = voxelSize;
            sparse.VolumeMin = volumeMin;
            sparse.CellCount = cellCount;
            sparse.Pages = pages;
            sparse.SlotCells = slotCells;
            output = sparse;
            texture = sparse;
        }

        public void CollectVoxelizationPasses(ProcessedVoxelVolume data, VoxelStorageContext storageContext)
        {
            bool dynamic = phase == Phase.Done && data.Dynamic;
            if (dynamic)
            {
                CollectDynamicCells(data.DynamicBounds);
                if (dynamicCells.Count == 0)
                    return;
                if (dynamicFragments == null)
                {
                    dynamicFragments = Buffer.Typed.New(storageContext.device, DynamicBricks * 512 * Math.Max(1, storageUints), PixelFormat.R32_UInt, true);
                    clearDynamic = true;
                }
            }
            else if (phase != Phase.Mark && phase != Phase.Fill)
                return;

            var storer = new VoxelStorerSparse
            {
                Mark = phase == Phase.Mark,
                SlabStart = dynamic ? brickCount : slab * slabBricks,
                SlabCount = dynamic ? dynamicCells.Count : slabBricks,
                StorageUints = storageUints,
                Resolution = new Vector3(resolution.X, resolution.Y, resolution.Z),
                VolumeScale = volumeScale,
                VolumeOffset = -volumeMin * volumeScale,
                FragmentsBuffer = dynamic ? dynamicFragments : fragmentsBuffer,
                Pages = pages,
                Marks = marksBuffer,
            };
            if (!dynamic)
            {
                AddPasses(data, storer, volumeMin, storageContext.Extents, storer.Resolution);
                return;
            }

            // Views of DynamicView voxels on the grid, tiling each box unless one already covers it
            // (several bounds per figure); the writer keeps the maximum, overlaps do no harm.
            float size = DynamicView * voxelSize;
            views.Clear();
            foreach (var box in boxes)
            {
                Vector3 lo = volumeMin + (Vector3)box.Min * (voxelSize * 8), hi = lo + (Vector3)box.Size * (voxelSize * 8);
                if (views.Exists(v => Vector3.Min(v, lo) == v && Vector3.Max(v + new Vector3(size), hi) == v + new Vector3(size)))
                    continue;
                Vector3 tiles = new Vector3(MathF.Ceiling((hi.X - lo.X) / size), MathF.Ceiling((hi.Y - lo.Y) / size), MathF.Ceiling((hi.Z - lo.Z) / size));
                Vector3 start = (lo + hi) / 2 - tiles * (size / 2);
                for (int z = 0; z < tiles.Z; z++)
                    for (int y = 0; y < tiles.Y; y++)
                        for (int x = 0; x < tiles.X; x++)
                        {
                            Vector3 min = Snap(start + new Vector3(x, y, z) * size);
                            if (!views.Contains(min))
                                views.Add(min);
                        }
            }
            foreach (var min in views)
            {
                var view = new VoxelStorerSparse
                {
                    SlabStart = storer.SlabStart,
                    SlabCount = storer.SlabCount,
                    StorageUints = storer.StorageUints,
                    Resolution = storer.Resolution,
                    VolumeScale = storer.VolumeScale,
                    VolumeOffset = storer.VolumeOffset,
                    FragmentsBuffer = storer.FragmentsBuffer,
                    Pages = pages,
                };
                AddPasses(data, view, min, new Vector3(size), new Vector3(DynamicView));
            }
        }

        /// <summary>A corner on the voxel grid.</summary>
        Vector3 Snap(Vector3 p) => volumeMin + new Vector3(MathF.Round((p.X - volumeMin.X) / voxelSize),
            MathF.Round((p.Y - volumeMin.Y) / voxelSize), MathF.Round((p.Z - volumeMin.Z) / voxelSize)) * voxelSize;

        /// <summary>The voxelisation passes of a storer rasterising the box from <paramref name="min"/>.</summary>
        static void AddPasses(ProcessedVoxelVolume data, VoxelStorerSparse storer, Vector3 min, Vector3 size, Vector3 viewport)
        {
            storer.ViewScale = new Vector3(1f / size.X, 1f / size.Y, 1f / size.Z);
            storer.ViewOffset = -min * storer.ViewScale;
            Matrix toView = Matrix.Scaling(size) * Matrix.Translation(min + size / 2);
            toView.Invert();
            toView *= Matrix.Scaling(2f, 2f, 2f);
            foreach (var attr in data.Attributes)
                attr.Attribute.CollectVoxelizationPasses(data.passList, storer, toView, viewport, attr.Stage, attr.Output);
        }

        ComputeEffectShader marks;
        ComputeEffectShader dilate;
        ComputeEffectShader bufferToBricks;
        ComputeEffectShader clearCounter;
        ComputeEffectShader clearFragments;
        ComputeEffectShader clearMarks;
        ComputeEffectShader clearDynamicFragments;
        ComputeEffectShader editPages;
        ComputeEffectShader editCopy;
        bool clearDynamic;
        bool pagesCleared;

        public void PostProcess(VoxelStorageContext storageContext, RenderDrawContext drawContext, ProcessedVoxelVolume data)
        {
            var commandList = drawContext.CommandList;
            var context = drawContext.RenderContext;
            marks ??= new ComputeEffectShader(context) { ShaderSourceName = "SparseMarks" };
            dilate ??= new ComputeEffectShader(context) { ShaderSourceName = "SparseMarks" };
            bufferToBricks ??= new ComputeEffectShader(context) { ShaderSourceName = "BufferToBricksEffect" };

            switch (phase)
            {
                case Phase.Mark:
                    // The marks of this frame are counted; once the count holds, slots are given.
                    bool stable = counts.Stable(commandList, (int)phase, out uint marked);
                    Clear(drawContext, counter);
                    RunMarks(drawContext, stable && marked > 0);
                    if (stable && marked > 0)
                    {
                        StartFill(drawContext, (int)marked);
                        break;
                    }
                    counts.Request(commandList, (int)phase);
                    break;

                case Phase.Fill:
                    bool written = counts.Stable(commandList, (int)phase * 4096 + slab, out uint voxels);
                    Clear(drawContext, counter);
                    RunBufferToBricks(drawContext, data);
                    if (written && voxels > 0)
                    {
                        Console.WriteLine($"[voxel] slab {slab + 1}/{SlabCount}: {voxels} voxels");
                        if (++slab >= SlabCount)
                        {
                            phase = Phase.Levels;
                            break;
                        }
                    }
                    counts.Request(commandList, (int)phase * 4096 + slab);
                    break;

                case Phase.Levels:
                    // The attribute's PostProcess comes next and builds them. What
                    // only the bake needs goes.
                    output.BuildLevels = true;
                    phase = Phase.Done;
                    staticPages = Texture.New3D(drawContext.GraphicsDevice, cellCount.X, cellCount.Y, cellCount.Z, false, PixelFormat.R32_UInt,
                        TextureFlags.ShaderResource);
                    commandList.Copy(pages, staticPages);
                    fragmentsBuffer.Dispose();
                    fragmentsBuffer = null;
                    marksBuffer.Dispose();
                    marksBuffer = null;
                    break;
            }
        }

        /// <summary>Vulkan has no clear for buffers in Stride; ClearBuffer does it.</summary>
        void Clear(RenderDrawContext drawContext, Buffer buffer)
        {
            ref ComputeEffectShader shader = ref buffer == counter ? ref clearCounter : ref buffer == marksBuffer ? ref clearMarks
                : ref buffer == dynamicFragments ? ref clearDynamicFragments : ref clearFragments;
            shader ??= new ComputeEffectShader(drawContext.RenderContext) { ShaderSourceName = "ClearBuffer" };
            shader.ThreadNumbers = new Int3(256, 1, 1);
            shader.ThreadGroupCounts = LinearDispatch(buffer.ElementCount, 256, out int rowLength);
            shader.Parameters.Set(ClearBufferKeys.buffer, buffer);
            shader.Parameters.Set(ClearBufferKeys.offset, 0);
            shader.Parameters.Set(ClearBufferKeys.rowLength, rowLength);
            shader.Parameters.Set(ClearBufferKeys.count, buffer.ElementCount);
            ((RendererBase)shader).Draw(drawContext);
        }

        int SlabCount => (brickCount + slabBricks - 1) / slabBricks;

        void RunMarks(RenderDrawContext drawContext, bool allocate)
        {
            if (!pagesCleared)
            {
                drawContext.CommandList.ClearReadWrite(pages, UInt4.Zero);
                Clear(drawContext, marksBuffer);
                pagesCleared = true;
                return;
            }
            foreach (var shader in new[] { dilate, marks })
            {
                shader.ThreadNumbers = new Int3(8);
                shader.ThreadGroupCounts = (cellCount + new Int3(7)) / 8;
                shader.Parameters.Set(SparseMarksKeys.Marks, marksBuffer);
                shader.Parameters.Set(SparseMarksKeys.cellCount, cellCount);
                shader.Parameters.Set(SparseMarksKeys.Pages, pages);
                shader.Parameters.Set(SparseMarksKeys.SlotCells, slotCells);
                shader.Parameters.Set(SparseMarksKeys.Counter, counter);
                shader.Parameters.Set(SparseMarksKeys.Dilate, shader == dilate ? 1u : 0u);
                shader.Parameters.Set(SparseMarksKeys.Allocate, allocate ? 1u : 0u);
                ((RendererBase)shader).Draw(drawContext);
            }
        }

        void StartFill(RenderDrawContext drawContext, int count)
        {
            // Room for cells marked after the count was read: slots past it would
            // point outside the atlas.
            brickCount = Math.Min(count + count / 16 + 64, MaxBricks - DynamicBricks);
            slabBricks = Math.Min(brickCount, MaxFragments / (512 * Math.Max(1, storageUints)));
            slab = 0;
            Console.WriteLine($"[voxel] {count} bricks (room for {brickCount}) of 8^3 ({voxelSize:F3} m), {SlabCount} slab(s), {storageUints} uint per voxel");

            fragmentsBuffer?.Dispose();
            fragmentsBuffer = Buffer.Typed.New(drawContext.GraphicsDevice, slabBricks * 512 * storageUints, PixelFormat.R32_UInt, true);
            Clear(drawContext, fragmentsBuffer);
            output.BrickCount = brickCount;
            output.CreateAtlases(drawContext.GraphicsDevice, drawContext.CommandList, DynamicBricks);
            phase = Phase.Fill;
        }

        void RunBufferToBricks(RenderDrawContext drawContext, ProcessedVoxelVolume data)
        {
            int start = slab * slabBricks;
            RunBufferToBricks(drawContext, data, fragmentsBuffer, start, Math.Min(slabBricks, brickCount - start));
        }

        void RunBufferToBricks(RenderDrawContext drawContext, ProcessedVoxelVolume data, Buffer fragments, int start, int count)
        {
            if (!ApplyWriterParameters(data, bufferToBricks, fragments))
                return;
            bufferToBricks.ThreadNumbers = new Int3(256, 1, 1);
            bufferToBricks.ThreadGroupCounts = LinearDispatch(count * 512, 256, out int rowLength);
            bufferToBricks.Parameters.Set(BufferToBricksKeys.Pages, pages);
            bufferToBricks.Parameters.Set(BufferToBricksKeys.SlotCells, slotCells);
            bufferToBricks.Parameters.Set(BufferToBricksKeys.Counter, counter);
            bufferToBricks.Parameters.Set(BufferToBricksKeys.slabStart, (uint)start);
            bufferToBricks.Parameters.Set(BufferToBricksKeys.slabCount, (uint)count);
            bufferToBricks.Parameters.Set(BufferToBricksKeys.rowLength, (uint)rowLength);
            bufferToBricks.Parameters.Set(BufferToBricksKeys.atlasBricks, output.AtlasBricks);
            ((RendererBase)bufferToBricks).Draw(drawContext);
        }

        /// <summary>
        /// Group counts for <paramref name="count"/> threads in groups of
        /// <paramref name="threads"/>, spread over X and Y (65535 groups at most
        /// per dimension); the shader recomposes the index with rowLength.
        /// </summary>
        public static Int3 LinearDispatch(int count, int threads, out int rowLength)
        {
            int groups = Math.Max(1, (count + threads - 1) / threads);
            int groupsX = Math.Min(groups, 65535);
            rowLength = groupsX * threads;
            return new Int3(groupsX, (groups + groupsX - 1) / groupsX, 1);
        }

        /// <summary>VoxelStorageClipmaps.ApplyWriterParameters for the bricks: attributes, layouts and macros.</summary>
        bool ApplyWriterParameters(ProcessedVoxelVolume data, ComputeEffectShader writer, Buffer fragments)
        {
            var indirectVoxels = new List<VoxelAttribute>();
            var tempVoxels = new List<VoxelAttribute>();
            var indirect = new ShaderSourceCollection();
            var temp = new ShaderSourceCollection();

            int sampleIndex = 0;
            foreach (var attr in data.Attributes)
            {
                attr.Attribute.LocalSamplerID = sampleIndex++;
                if (attr.Attribute.RequiresColumns())
                    return false;
            }
            foreach (var attr in data.Attributes)
            {
                if (attr.Stage != VoxelizationStage.Post) continue;
                if (attr.Output)
                {
                    indirect.Add(attr.Attribute.GetVoxelizationShader());
                    indirectVoxels.Add(attr.Attribute);
                }
                else
                {
                    temp.Add(attr.Attribute.GetVoxelizationShader());
                    tempVoxels.Add(attr.Attribute);
                }
            }
            for (int i = 0; i < indirectVoxels.Count; i++)
                indirectVoxels[i].UpdateVoxelizationLayout($"AttributesIndirect[{i}]");
            for (int i = 0; i < tempVoxels.Count; i++)
                tempVoxels[i].UpdateVoxelizationLayout($"AttributesTemp[{i}]");
            foreach (var attr in data.Attributes)
                attr.Attribute.ApplyVoxelizationParameters(writer.Parameters);

            writer.Parameters.Set(BufferToTextureKeys.VoxelFragments, fragments);
            writer.Parameters.Set(BufferToTextureKeys.storageUints, storageUints);

            string readAndStore = "";
            string store = "";
            for (int i = 0; i < tempVoxels.Count; i++)
            {
                string sample = tempVoxels[i].LocalSamplerID.ToString();
                readAndStore += $"AttributesTemp[{i}].InitializeFromBuffer(VoxelFragments, VoxelFragmentsIndex + {tempVoxels[i].BufferOffset}, uint2({tempVoxels[i].BufferOffset} + initialVoxelFragmentsIndex, yStride));\n" +
                                $"streams.LocalSample[{sample}] = AttributesTemp[{i}].SampleLocal();\n\n";
                store += $"streams.LocalSample[{sample}] = AttributesTemp[{i}].SampleLocal();\n";
            }
            for (int i = 0; i < indirectVoxels.Count; i++)
            {
                string sample = indirectVoxels[i].LocalSamplerID.ToString();
                readAndStore += $"AttributesIndirect[{i}].InitializeFromBuffer(VoxelFragments, VoxelFragmentsIndex + {indirectVoxels[i].BufferOffset}, uint2({indirectVoxels[i].BufferOffset} + initialVoxelFragmentsIndex, yStride));\n" +
                                $"streams.LocalSample[{sample}] = AttributesIndirect[{i}].SampleLocal();\n\n";
                store += $"streams.LocalSample[{sample}] = AttributesIndirect[{i}].SampleLocal();\n";
            }
            writer.Parameters.Set(BufferToTextureKeys.AttributesIndirect, indirect);
            writer.Parameters.Set(BufferToTextureKeys.AttributesTemp, temp);
            writer.Parameters.Set(BufferToTextureKeys.IndirectReadAndStoreMacro, readAndStore);
            writer.Parameters.Set(BufferToTextureKeys.IndirectStoreMacro, store);
            return true;
        }

        /// <summary>The cells round the moving groups (as many as there are dynamic bricks) and their boxes.</summary>
        void CollectDynamicCells(List<BoundingBox> bounds)
        {
            lastCells.Clear();
            lastCells.AddRange(dynamicCells);
            lastBoxes.Clear();
            lastBoxes.AddRange(boxes);
            dynamicCells.Clear();
            boxes.Clear();
            cellSet.Clear();

            float cellSize = voxelSize * 8;
            float margin = voxelSize * DynamicMargin;
            foreach (var bound in bounds)
            {
                Int3 min = Int3.Max(Cell(bound.Minimum - new Vector3(margin), cellSize), Int3.Zero);
                Int3 max = Int3.Min(Cell(bound.Maximum + new Vector3(margin), cellSize), cellCount - new Int3(1));
                if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                    continue;
                boxes.Add((min, max - min + new Int3(1)));
                for (int z = min.Z; z <= max.Z; z++)
                    for (int y = min.Y; y <= max.Y; y++)
                        for (int x = min.X; x <= max.X; x++)
                            if (dynamicCells.Count < DynamicBricks && cellSet.Add(new Int3(x, y, z)))
                                dynamicCells.Add(new Int3(x, y, z));
            }
        }

        Int3 Cell(Vector3 position, float cellSize)
        {
            Vector3 c = (position - volumeMin) / cellSize;
            return new Int3((int)MathF.Floor(c.X), (int)MathF.Floor(c.Y), (int)MathF.Floor(c.Z));
        }

        static uint Pack(Int3 cell) => (uint)(cell.X | (cell.Y << 8) | (cell.Z << 16));

        /// <summary>The cells of last frame read their baked bricks again, those of this frame their own copies.</summary>
        public void RestoreDynamic(RenderDrawContext drawContext, ProcessedVoxelVolume data)
        {
            if (phase != Phase.Done || lastCells.Count + dynamicCells.Count == 0)
                return;
            var context = drawContext.RenderContext;
            editData ??= new uint[DynamicBricks * 4];
            edits ??= Buffer.Typed.New(drawContext.GraphicsDevice, editData.Length, PixelFormat.R32_UInt, true);
            editPages ??= new ComputeEffectShader(context) { ShaderSourceName = "SparseEdit" };
            editCopy ??= new ComputeEffectShader(context) { ShaderSourceName = "SparseEdit" };
            if (clearDynamic)
            {
                Clear(drawContext, dynamicFragments);
                clearDynamic = false;
            }

            editCount = 0;
            foreach (var cell in lastCells)
                if (!cellSet.Contains(cell))
                {
                    editData[editCount * 2] = Pack(cell);
                    editData[editCount * 2 + 1] = 0;
                    editCount++;
                }
            for (int i = 0; i < dynamicCells.Count; i++)
            {
                editData[editCount * 2] = Pack(dynamicCells[i]);
                editData[editCount * 2 + 1] = (uint)(brickCount + i + 1);
                editCount++;
            }
            edits.SetData(drawContext.CommandList, editData);

            foreach (var shader in new[] { editPages, editCopy })
            {
                bool copy = shader == editCopy;
                shader.ThreadNumbers = new Int3(256, 1, 1);
                shader.ThreadGroupCounts = LinearDispatch(editCount * (copy ? 729 : 1), 256, out int rowLength);
                shader.Parameters.Set(SparseEditKeys.Pages, pages);
                shader.Parameters.Set(SparseEditKeys.StaticPages, staticPages);
                shader.Parameters.Set(SparseEditKeys.SlotCells, slotCells);
                shader.Parameters.Set(SparseEditKeys.Edits, edits);
                shader.Parameters.Set(SparseEditKeys.Atlas, output.Atlases[0]);
                shader.Parameters.Set(SparseEditKeys.atlasBricks, output.AtlasBricks);
                shader.Parameters.Set(SparseEditKeys.editCount, (uint)editCount);
                shader.Parameters.Set(SparseEditKeys.rowLength, (uint)rowLength);
                shader.Parameters.Set(SparseEditKeys.Copy, copy ? 1u : 0u);
                ((RendererBase)shader).Draw(drawContext);
            }
        }

        /// <summary>The moving groups' fragments into their bricks, then the coarser levels there and in last frame's boxes.</summary>
        public void PostProcessDynamic(VoxelStorageContext storageContext, RenderDrawContext drawContext, ProcessedVoxelVolume data)
        {
            if (phase != Phase.Done)
                return;
            bufferToBricks ??= new ComputeEffectShader(drawContext.RenderContext) { ShaderSourceName = "BufferToBricksEffect" };
            if (dynamicCells.Count > 0)
            {
                RunBufferToBricks(drawContext, data, dynamicFragments, brickCount, dynamicCells.Count);
                output.BuildBrickLevels(drawContext, brickCount, dynamicCells.Count);
            }
            foreach (var box in lastBoxes)
                output.BuildDense(drawContext, box.Min, box.Size);
            foreach (var box in boxes)
                output.BuildDense(drawContext, box.Min, box.Size);
        }

        /// <summary>
        /// Reads the counter back without stalling: a copy into a staging buffer,
        /// mapped a few frames later once the GPU is past it. A count holds when
        /// two readings of the same step in a row agree within 0.1 %: the voxel count
        /// wavers by a few thousand from frame to frame, an exact match took up to 11 s,
        /// while a frame still missing an effect lacks whole meshes.
        /// </summary>
        sealed class CountReader
        {
            Buffer counter;
            readonly Buffer[] staging = new Buffer[4];
            readonly int[] tags = new int[4];
            int next;
            int pending;
            int lastTag = -1;
            uint last;
            int agreeing;

            public void Create(GraphicsDevice device, Buffer source)
            {
                counter = source;
                for (int i = 0; i < staging.Length; i++)
                    staging[i] ??= Buffer.New(device, source.SizeInBytes, BufferFlags.None, GraphicsResourceUsage.Staging);
                next = pending = agreeing = 0;
                lastTag = -1;
            }

            public void Request(CommandList commandList, int tag)
            {
                if (pending == staging.Length)
                    return;
                int i = (next + pending) % staging.Length;
                commandList.Copy(counter, staging[i]);
                tags[i] = tag;
                pending++;
            }

            public unsafe bool Stable(CommandList commandList, int tag, out uint value)
            {
                value = 0;
                while (pending > 0)
                {
                    var mapped = commandList.MapSubResource(staging[next], 0, MapMode.Read, doNotWait: true);
                    if (mapped.DataBox.DataPointer == IntPtr.Zero)
                        break;
                    uint read = *(uint*)mapped.DataBox.DataPointer;
                    commandList.UnmapSubResource(mapped);
                    int readTag = tags[next];
                    next = (next + 1) % staging.Length;
                    pending--;
                    if (readTag != tag)
                        continue;
                    agreeing = readTag == lastTag && read > 0 && Math.Abs((long)read - last) * 1000 <= last ? agreeing + 1 : 0;
                    lastTag = readTag;
                    last = read;
                }
                value = last;
                return lastTag == tag && agreeing >= 1;
            }
        }
    }
}
