using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Stride.Core.Serialization;
using Stride.Rendering;
using Stride.Shaders.Compiler;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// KOTH_EFFECTLOG=path: appends every effect permutation the game asks for
    /// to path, once each, in Stride's binary form (what the remote effect compiler
    /// sends Game Studio). Tools/effects/EffectLog.cs turns the recordings into
    /// Assets/EffectCompileLog.sdeffectlog, whose permutations the asset compiler
    /// then builds into the bundle: no PC compiles them at runtime any more.
    /// </summary>
    public static class EffectRecorder
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "EffectUsed")]
        static extern ref Action<EffectCompileRequest, CompilerResults> EffectUsed(EffectSystem system);

        static readonly HashSet<EffectCompileRequest> seen = new HashSet<EffectCompileRequest>();
        static FileStream file;

        /// <summary>Starts recording when KOTH_EFFECTLOG is set.</summary>
        public static void Attach(EffectSystem system)
        {
            string path = Environment.GetEnvironmentVariable("KOTH_EFFECTLOG");
            if (string.IsNullOrEmpty(path) || system == null || file != null)
                return;
            file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            EffectUsed(system) += Record;
            Console.WriteLine($"[effects] recording to {path}");
        }

        // Called from the render threads; each record is its length, then the request.
        static void Record(EffectCompileRequest request, CompilerResults result)
        {
            if (result.HasErrors)
                return;
            var memory = new MemoryStream();
            var writer = new BinarySerializationWriter(memory);
            writer.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
            writer.SerializeExtended(request, ArchiveMode.Serialize);
            lock (seen)
            {
                if (!seen.Add(request))
                    return;
                file.Write(BitConverter.GetBytes((int)memory.Length));
                memory.WriteTo(file);
                file.Flush();
                Console.WriteLine($"[effects] {seen.Count}: {request.EffectName}");
            }
        }
    }
}
