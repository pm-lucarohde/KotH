using System.Diagnostics;
using Stride.Audio;

namespace KotH.Client.Audio
{
    /// <summary>Watches one sound that should be playing (music, ambience) twice a second, as OpenAL sees it:
    /// silent for over a second while the engine runs, it should be started anew (Silent says why). Silent
    /// cases seen: played while the window was in the background, a stopped source, a failed stream worker.</summary>
    internal sealed class SoundWatch
    {
        const double Every = 0.5, Grace = 1.5, Limit = 1.0, Window = 60.0, Pause = 30.0;
        const int MostRestarts = 5;

        readonly Stopwatch clock = Stopwatch.StartNew();
        double next, silentSince = -1.0, graceUntil, lastPosition = -1.0, windowFrom, pausedUntil;
        int restarts;

        /// <summary>A new instance was started: a moment to buffer before it is judged.</summary>
        public void Started()
        {
            graceUntil = clock.Elapsed.TotalSeconds + Grace;
            silentSince = lastPosition = -1.0;
        }

        /// <summary>Whether the instance should be started again; then why, for the log.</summary>
        public bool Silent(SoundInstance instance, out string why)
        {
            why = null;
            double now = clock.Elapsed.TotalSeconds;
            if (instance == null || now < next || now < pausedUntil)
                return false;
            next = now + Every;
            if (!NativeSound.EngineRunning(instance))
            {
                silentSince = -1.0;
                return false;
            }
            if (NativeSound.StreamWorker is { IsFaulted: true } worker)
            {
                why = $"stream worker failed ({worker.Exception?.InnerException?.GetType().Name}: {worker.Exception?.InnerException?.Message})";
                NativeSound.ForgetWorker();
                return Allowed(now, ref why);
            }
            if (now < graceUntil)
                return false;
            double position = NativeSound.Position(instance);
            bool playing = NativeSound.Playing(instance), moving = position != lastPosition;
            lastPosition = position;
            if (playing && moving)
            {
                silentSince = -1.0;
                return false;
            }
            if (silentSince < 0.0)
                silentSince = now;
            if (now - silentSince < Limit)
                return false;
            why = playing ? $"OpenAL plays but stays at {position:F2} s" : $"OpenAL does not play it (Stride says {instance.PlayState})";
            return Allowed(now, ref why);
        }

        // At most MostRestarts a minute, then a pause: a sound that never plays is not started over and over.
        bool Allowed(double now, ref string why)
        {
            if (now - windowFrom > Window)
                (windowFrom, restarts) = (now, 0);
            if (++restarts <= MostRestarts)
                return true;
            pausedUntil = now + Pause;
            why += $"; {MostRestarts} restarts this minute, next try in {Pause:F0} s";
            restarts = 0;
            windowFrom = pausedUntil;
            return false;
        }
    }
}
