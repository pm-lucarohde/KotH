using System;
using Stride.Audio;

namespace KotH.Client.Audio
{
    /// <summary>One speaker's voice as a stream for Stride's audio worker: the game thread writes decoded
    /// 20 ms frames, the worker takes them out (silence while there are none, so the stream never runs dry).
    /// A spurt starts once 60 ms are waiting, against the network's jitter; any fault plays silence.</summary>
    public sealed class VoiceSource : DynamicSoundSource
    {
        const int Buffers = 4, Frame = VoiceCapture.Frame, Ring = VoiceCapture.Rate;   // a second at most
        const int Prebuffer = 3 * Frame;

        readonly object gate = new object();
        readonly short[] ring = new short[Ring];
        readonly short[] chunk = new short[Frame];
        int read, count;
        bool flowing, faulted;

        VoiceSource() : base(null, Buffers, Frame * sizeof(short)) { }

        /// <summary>A playing 3D sound instance for a new speaker, mono 48 kHz; null without an audio engine.</summary>
        public static SoundInstance Create(AudioEngine engine, AudioListener listener, out VoiceSource source)
        {
            source = new VoiceSource();
            SoundInstance instance;
            try
            {
                instance = new SoundInstance(engine, listener, source, VoiceCapture.Rate, mono: true, spatialized: true);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[voice] no sound source: {e.Message}");
                return null;
            }
            source.soundInstance = instance;
            NewSources.Add(source);
            instance.Play();
            return instance;
        }

        public override int MaxNumberOfBuffers => Buffers;

        public override void SetLooped(bool looped) { }

        /// <summary>Game thread: a decoded frame; the oldest goes when a second has piled up.</summary>
        public void Write(ReadOnlySpan<short> frame)
        {
            lock (gate)
            {
                foreach (short s in frame)
                {
                    if (count == Ring)
                    {
                        read = (read + 1) % Ring;
                        count--;
                    }
                    ring[(read + count) % Ring] = s;
                    count++;
                }
            }
        }

        /// <summary>Milliseconds of voice waiting to be heard.</summary>
        public int QueuedMs
        {
            get
            {
                lock (gate)
                    return count * 1000 / VoiceCapture.Rate;
            }
        }

        // Stride's worker: the next 20 ms, voice once enough waits, else silence.
        protected override void ExtractAndFillData()
        {
            try
            {
                lock (gate)
                {
                    if (!flowing && count >= Prebuffer)
                        flowing = true;
                    if (flowing && count >= Frame)
                    {
                        for (int i = 0; i < Frame; i++)
                            chunk[i] = ring[(read + i) % Ring];
                        read = (read + Frame) % Ring;
                        count -= Frame;
                    }
                    else
                    {
                        flowing = false;
                        Array.Clear(chunk);
                    }
                }
                FillBuffer(chunk, Frame * sizeof(short), AudioLayer.BufferType.None);
            }
            catch (Exception e)
            {
                // The worker is every streamed sound's: a voice must never stop the music. Said once.
                if (!faulted)
                    StartLog.Warn($"voice playback fault, silence while it lasts: {e.GetType().Name}: {e.Message}");
                faulted = true;
            }
        }
    }
}
