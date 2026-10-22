// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using Stride.Graphics;
using Stride.Shaders;

namespace Stride.Rendering.Voxels
{
    public interface IVoxelStorage
    {
        void UpdateFromContext(VoxelStorageContext context);
        void CollectVoxelizationPasses(ProcessedVoxelVolume data, VoxelStorageContext storageContext);
        
        int RequestTempStorage(int count);
        void UpdateTexture(VoxelStorageContext context, ref IVoxelStorageTexture texture, Stride.Graphics.PixelFormat pixelFormat, int layoutSize);
        void UpdateTempStorage(VoxelStorageContext context);
        void PostProcess(VoxelStorageContext context, RenderDrawContext drawContext, ProcessedVoxelVolume data);

        // KotH patch: dynamic groups. A full voxelisation is kept; each frame the boxes
        // the moving groups were written to are restored from it, then they are written
        // into the boxes round where they are now.
        void KeepStatic(RenderDrawContext drawContext, ProcessedVoxelVolume data) { }
        void RestoreDynamic(RenderDrawContext drawContext, ProcessedVoxelVolume data) { }
        void PostProcessDynamic(VoxelStorageContext context, RenderDrawContext drawContext, ProcessedVoxelVolume data) => PostProcess(context, drawContext, data);
    }
}
