using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.Serialization;
using Stride.Rendering;
using Stride.Shaders.Compiler;

namespace KotH.Client.Rendering
{
    /// <summary>KOTH_EFFECTLOG=path: appends every effect permutation the game asks for, once
    /// each, in Stride's binary form. Tools/effects/EffectLog.cs turns them into
    /// Assets/EffectCompileLog.sdeffectlog, built into the bundle: no runtime compiles.
    /// Always: each one compiled at runtime goes to the start log, with its time.</summary>
    public static class EffectRecorder
    {
        // Requests by where their bytecode came from: the bundle, Stride's own cache (compiled on an earlier run), now.
        static int bundled, cached, compiled;
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "EffectUsed")]
        static extern ref Action<EffectCompileRequest, CompilerResults> EffectUsed(EffectSystem system);

        static readonly HashSet<EffectCompileRequest> seen = new HashSet<EffectCompileRequest>();
        static FileStream file;

        /// <summary>Starts recording when KOTH_EFFECTLOG is set.</summary>
        public static void Attach(EffectSystem system)
        {
            if (system != null && !noting)
            {
                noting = true;
                EffectUsed(system) += Note;
            }
            string path = Environment.GetEnvironmentVariable("KOTH_EFFECTLOG");
            if (string.IsNullOrEmpty(path) || system == null || file != null)
                return;
            file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            EffectUsed(system) += Record;
            Console.WriteLine($"[effects] recording to {path}");
        }

        static bool noting;

        /// <summary>The effects asked for so far, by where their bytecode came from.</summary>
        public static string Counts => $"{bundled} from the bundle, {cached} from the effect cache, {compiled} compiled";

        // Called from the render threads: the bundle's or the cache's bytecode is there at once, a compile is a task.
        static void Note(EffectCompileRequest request, CompilerResults result)
        {
            if (result.HasErrors)
            {
                StartLog.Warn($"effect {request.EffectName} failed to compile");
                return;
            }
            Task<EffectBytecodeCompilerResult> task = result.Bytecode.Task;
            if (task != null && !task.IsCompleted)
            {
                long at = StartLog.Now;
                Interlocked.Increment(ref compiled);
                task.ContinueWith(t => StartLog.Took($"effect compiled at runtime: {request.EffectName}", at), TaskScheduler.Default);
                return;
            }
            EffectBytecodeCacheLoadSource source = task != null ? task.Result.LoadSource : result.Bytecode.WaitForResult().LoadSource;
            if (source == EffectBytecodeCacheLoadSource.DynamicCache)
            {
                Interlocked.Increment(ref cached);
                StartLog.Mark($"effect from the effect cache, not the bundle: {request.EffectName}");
                if (file != null)
                    lock (seen)
                    {
                        Console.WriteLine($"[effects] not in the bundle: {request.EffectName}");
                        foreach (var p in request.UsedParameters)
                            Console.WriteLine($"[effects]    {p.Key} = {p.Value}");
                    }
            }
            else if (source == EffectBytecodeCacheLoadSource.JustCompiled)
                Interlocked.Increment(ref compiled);
            else
                Interlocked.Increment(ref bundled);
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
