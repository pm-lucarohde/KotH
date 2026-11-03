using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Stride.Audio;

namespace KotH.Client.Audio
{
    /// <summary>What OpenAL itself says about a sound, past Stride's own bookkeeping: a streamed instance
    /// reports Playing whatever its source does, and one played while the engine is paused (window in
    /// the background) stays silent. Also the one worker that streams every music and ambience.</summary>
    internal static class NativeSound
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Source")]
        static extern ref AudioLayer.Source SourceOf(SoundInstance instance);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "engine")]
        static extern ref AudioEngine EngineOf(SoundInstance instance);

        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "readFromDiskWorker")]
        static extern ref Task Worker(DynamicSoundSource type);

        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Sources")]
        static extern ref List<DynamicSoundSource> Sources(DynamicSoundSource type);

        /// <summary>Whether the engine can play at all now (not paused, not without a device).</summary>
        public static bool EngineRunning(SoundInstance instance) => EngineOf(instance)?.State == AudioEngineState.Running;

        /// <summary>Whether OpenAL plays the instance's source; false without a device.</summary>
        public static bool Playing(SoundInstance instance)
            => EngineOf(instance)?.State is AudioEngineState.Running or AudioEngineState.Paused && AudioLayer.SourceIsPlaying(SourceOf(instance));

        /// <summary>The source's play position in seconds, as OpenAL counts it (it moves while it plays).</summary>
        public static double Position(SoundInstance instance)
            => EngineOf(instance)?.State is AudioEngineState.Running or AudioEngineState.Paused ? AudioLayer.SourceGetPosition(SourceOf(instance)) : 0.0;

        /// <summary>The streaming worker, null before the first streamed sound; once faulted, every streamed sound is silent.</summary>
        public static Task StreamWorker => Worker(null);

        /// <summary>After the worker failed: forgets it and its sources, so the next streamed instance starts a new one.</summary>
        public static void ForgetWorker()
        {
            Worker(null) = null;
            Sources(null).Clear();
        }
    }
}
