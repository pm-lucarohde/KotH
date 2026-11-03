using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Stride.Audio;

namespace KotH.Client.Audio
{
    /// <summary>Stride keeps an engine without a device for the whole session (every sound a silent no-op).
    /// While that is so, a new engine is tried every 2 s, the settings' device first, then the default one;
    /// once one opens, the loaded sounds move over to it (memory buffers filled) and the music and sounds
    /// start as at a normal start: until then they made no instances.</summary>
    public static class AudioRevive
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_AudioEngine")]
        static extern void SetEngine(AudioSystem system, AudioEngine engine);

        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "audioEngineSingleton")]
        static extern ref AudioEngine Singleton(AudioSystem type);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "notDisposedSounds")]
        static extern ref List<SoundBase> SoundsOf(AudioEngine engine);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RegisterSound")]
        static extern void Register(AudioEngine engine, SoundBase sound);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AttachEngine")]
        static extern void Attach(SoundBase sound, AudioEngine engine);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "LoadSoundInMemory")]
        static extern void LoadInMemory(Sound sound);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StreamFromDisk")]
        static extern bool Streamed(Sound sound);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CompressedDataUrl")]
        static extern string DataUrl(Sound sound);

        const double Every = 2.0;
        static readonly Stopwatch since = Stopwatch.StartNew();
        static double nextAt = Every;

        /// <summary>Once per frame; does nothing while the engine has its device.</summary>
        public static void Tick(AudioSystem audio, bool active)
        {
            if (audio?.AudioEngine is not { State: AudioEngineState.Invalidated } old || since.Elapsed.TotalSeconds < nextAt)
                return;
            nextAt = since.Elapsed.TotalSeconds + Every;
            AudioDevice wanted = audio.RequestedAudioDevice ?? new AudioDevice();
            AudioEngine fresh = Open(wanted) ?? (wanted.Name != "default" ? Open(new AudioDevice()) : null);
            if (fresh == null)
                return;

            int moved = 0;
            List<SoundBase> sounds = SoundsOf(old);
            try
            {
                lock (sounds)
                {
                    foreach (SoundBase sound in sounds)
                    {
                        Attach(sound, fresh);
                        Register(fresh, sound);
                        if (sound is Sound s && !Streamed(s) && DataUrl(s) != null)
                            LoadInMemory(s);
                        moved++;
                    }
                    sounds.Clear();
                }
            }
            catch (System.Exception e)
            {
                // Half moved: stays silent (as without a device) rather than stopping the game.
                nextAt = double.PositiveInfinity;
                StartLog.Warn($"audio device opened, but moving the sounds failed: {e.GetType().Name}: {e.Message}");
                return;
            }
            fresh.MasterVolume = old.MasterVolume;
            if (!active)
                fresh.PauseAudio();
            SetEngine(audio, fresh);
            Singleton(null) = fresh;
            StartLog.Mark($"audio device opened after {since.Elapsed.TotalSeconds:F0} s ({fresh.State}), {moved} sounds moved to it");
        }

        // The old engine without a device is dropped, never disposed: that would stop the stream worker.
        static AudioEngine Open(AudioDevice device)
        {
            try
            {
                AudioEngine engine = AudioEngineFactory.NewAudioEngine(device);
                return engine.State == AudioEngineState.Invalidated ? null : engine;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
