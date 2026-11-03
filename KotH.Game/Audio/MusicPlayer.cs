using System;
using System.Collections.Generic;
using KotH.Client.Settings;
using KotH.Shared.Match;
using Stride.Audio;
using Stride.Core;
using Stride.Engine;
using Stride.Media;

namespace KotH.Client.Audio
{
    /// <summary>The music: one looping piece in the main menu, one before the round (lobby, countdown
    /// and the pause after a win), a fixed piece when a round starts and then the playlist in
    /// shuffled order; every change crossfades. Follows the confirmed round when networked, so no
    /// rollback flips it. Presentation only.</summary>
    [DataContract("KotH.MusicPlayer")]
    public sealed class MusicPlayer : SyncScript
    {
        /// <userdoc>Loops while the main menu is open.</userdoc>
        [Display("Main menu", "Music")] public Sound Menu;

        /// <userdoc>Loops while no game runs: waiting for players, countdown and the pause after a win.</userdoc>
        [Display("Before the game", "Music")] public Sound Lobby;

        /// <userdoc>Plays once when a round starts.</userdoc>
        [Display("Round start", "Music")] public Sound RoundStart;

        /// <userdoc>After the round start track, shuffled: each once before any repeats.</userdoc>
        [Display("Then shuffled", "Music")] public List<Sound> Playlist = new List<Sound>();

        /// <userdoc>At the Music setting's 80 %.</userdoc>
        [Display("Volume", "Music")] public float Volume = 0.1224f;

        /// <userdoc>Scales music, ambience and sounds together, keeping their balance. 2.43 lifts everything until a hit at one's own ear on a music peak reaches about −0.3 dBFS at most (music about −22 LUFS); at the Overall setting's 80 %.</userdoc>
        [Display("Master volume", "All")] public float MasterVolume = 2.43f;

        /// <userdoc>How long one track crossfades into the next (seconds), also when one runs out: the next starts that long before its end.</userdoc>
        [Display("Crossfade", "Music")] public float FadeSeconds = 3.5f;

        [DataMemberIgnore] public HillMatch Match { get; set; }

        /// <summary>The main menu is open: its piece plays, whatever the round does.</summary>
        [DataMemberIgnore] public bool InMenu { get; set; }

        /// <summary>Networked: the round as the host confirmed it, null before the first.</summary>
        [DataMemberIgnore] public Func<MatchState?> ConfirmedRound { get; set; }

        /// <summary>Test aid: start every track this many seconds before its end.</summary>
        [DataMemberIgnore] public double TailSeconds { get; set; }

        // A streamed instance keeps reporting Playing after its end, so the player times the piece
        // itself and starts the next one a fade before. Every start gets a new instance: one played
        // again after Stop replayed old buffers for seconds.

        readonly List<Sound> deck = new List<Sound>();
        readonly Random random = new Random();

        SoundInstance current, fading;
        Sound playing;
        float fadeIn = 1f;
        double age, length;
        Mode? mode;
        readonly List<string> history = new List<string>();
        float master = -1f;
        readonly SoundWatch watch = new SoundWatch();
        // A failure silences the music for a while, never for good: again after 2 s, after five in a row 30 s.
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double retryAt;
        int failures;
        bool noEngineLogged;

        public override void Update()
        {
            if (clock.Elapsed.TotalSeconds < retryAt)
                return;
            // Not while the first frame is made (compiles fill the thread pool, the stream start waits on it).
            if (Game.DrawTime.FrameCount < 1)
                return;
            if (Audio.AudioEngine is not { State: not AudioEngineState.Invalidated })
            {
                if (!noEngineLogged)
                    StartLog.Warn("music: no audio device, music and sounds stay off this session");
                noEngineLogged = true;
                return;
            }
            try
            {
                // The listener's gain: one factor over every sound, allowed above 1; times Overall.
                float wanted = MasterVolume * PlayerSettings.Gain(ClientSettings.Applied.Overall);
                if (master != wanted)
                    Audio.AudioEngine.MasterVolume = master = wanted;
                // Joining, the round comes with the host's map: until then it is the lobby.
                if (!InMenu && Match == null && ConfirmedRound == null)
                    return;
                Step((float)Game.UpdateTime.Elapsed.TotalSeconds);
                if (watch.Silent(current, out string why))
                {
                    StartLog.Warn($"music {NameOf(playing)} silent: {why}; started again");
                    Restart();
                }
            }
            catch (Exception e)
            {
                // A broken asset or device: silent for a while, never a stop of the game.
                failures++;
                retryAt = clock.Elapsed.TotalSeconds + (failures % 5 == 0 ? 30.0 : 2.0);
                Log.Warning($"Music fails: {e.Message}");
                StartLog.Warn($"music fails ({failures}), again in {retryAt - clock.Elapsed.TotalSeconds:F0} s: {e}");
                StopAll();
                mode = null;
            }
        }

        /// <summary>The piece playing now as a new instance, where it stood (a looping one from its start).</summary>
        void Restart()
        {
            if (playing == null)
                return;
            SoundInstance instance = playing.CreateInstance();
            instance.IsLooping = current?.IsLooping ?? false;
            instance.Volume = current?.Volume ?? 0f;
            if (!instance.IsLooping && age > 0.0 && age < length - 1.0)
            {
                TimeSpan from = playing.TotalLength - TimeSpan.FromSeconds(length - age);
                instance.SetRange(new PlayRange(from, playing.TotalLength - from));
            }
            Release(current);
            instance.Play();
            current = instance;
            confirmed = false;
            confirmAt = age + 2.0;
            watch.Started();
        }

        public override void Cancel() => StopAll();

        /// <summary>For tests: what plays, how it stands and how often the music changed.</summary>
        [DataMemberIgnore]
        public string Readout
            => $"{NameOf(playing) ?? "nothing"}, " +
               $"{current?.PlayState.ToString() ?? "-"} for {age:F1} of {length:F1} s, volume {current?.Volume ?? 0f:F2}, history {string.Join(" > ", history)}";

        void Step(float dt)
        {
            age += dt;
            Confirm();
            Mode now = InMenu ? Mode.Menu : Phase() == MatchPhase.Running ? Mode.Round : Mode.Lobby;
            if (now != mode)
            {
                mode = now;
                if (now == Mode.Menu)
                    Switch(Menu, "main menu");
                else
                    Switch(now == Mode.Round ? RoundStart : Lobby, now == Mode.Round ? "round start" : "before the game");
            }
            else if (now == Mode.Round && current != null && !current.IsLooping && age >= length - Math.Min(FadeSeconds, length * 0.5))
                Switch(Draw(), "shuffle");

            float step = FadeSeconds > 0f ? dt / FadeSeconds : 1f;
            fadeIn = Math.Min(1f, fadeIn + step);
            float volume = Volume * PlayerSettings.Gain(ClientSettings.Applied.Music);
            if (current != null)
                current.Volume = volume * fadeIn;
            if (fading != null)
            {
                fading.Volume = volume * (1f - fadeIn);
                if (fadeIn >= 1f)
                {
                    Release(fading);
                    fading = null;
                }
            }
        }

        MatchPhase Phase()
        {
            if (ConfirmedRound == null)
                return Match.State.Phase;
            return ConfirmedRound() is { } confirmed ? confirmed.Phase : MatchPhase.Waiting;
        }

        /// <summary>
        /// Crossfades from what plays now to the sound; null fades to silence.
        /// </summary>
        void Switch(Sound sound, string what)
        {
            Release(fading);
            fading = current;
            current = null;
            playing = sound;
            fadeIn = fading != null ? 0f : 1f;
            age = 0.0;
            if (sound == null)
                return;

            SoundInstance instance = sound.CreateInstance();
            instance.IsLooping = sound == Lobby || sound == Menu;
            instance.Volume = Volume * PlayerSettings.Gain(ClientSettings.Applied.Music) * fadeIn;
            length = sound.TotalLength.TotalSeconds;
            if (TailSeconds > 0.0)
            {
                TimeSpan from = sound.TotalLength - TimeSpan.FromSeconds(Math.Min(TailSeconds, length));
                instance.SetRange(new PlayRange(from, sound.TotalLength - from));
                length -= from.TotalSeconds;
            }
            instance.Play();
            current = instance;
            confirmed = false;
            confirmAt = 2.0;
            watch.Started();
            if (history.Count < 16)
                history.Add(NameOf(sound));
            Log.Info($"Music: {what} ({NameOf(sound)})");
        }

        bool confirmed;
        double confirmAt;

        // Two seconds into a piece: whether OpenAL really plays it, for the start log.
        void Confirm()
        {
            if (confirmed || current == null || age < confirmAt)
                return;
            confirmed = true;
            bool audible = NativeSound.Playing(current);
            if (audible)
                failures = 0;
            string worker = NativeSound.StreamWorker is { IsFaulted: true } w ? $", stream worker FAULTED ({w.Exception?.InnerException?.Message})" : "";
            StartLog.Mark($"music {NameOf(playing)}: {current.PlayState}, OpenAL {(audible ? "playing" : "SILENT")} " +
                          $"at {NativeSound.Position(current):F2} s, engine {Audio.AudioEngine?.State}{worker}");
        }

        string NameOf(Sound sound)
            => sound != null && Content.TryGetAssetUrl(sound, out string url) ? url[(url.LastIndexOf('/') + 1)..] : null;

        /// <summary>The next piece of the shuffled playlist, never the one just played.</summary>
        Sound Draw()
        {
            if (deck.Count == 0)
            {
                foreach (Sound sound in Playlist)
                    if (sound != null)
                        deck.Add(sound);
                for (int i = deck.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (deck[i], deck[j]) = (deck[j], deck[i]);
                }
                if (deck.Count > 1 && deck[0] == playing)
                    (deck[0], deck[1]) = (deck[1], deck[0]);
            }
            if (deck.Count == 0)
                return null;
            Sound next = deck[0];
            deck.RemoveAt(0);
            return next;
        }

        void StopAll()
        {
            Release(current);
            Release(fading);
            current = fading = null;
        }

        enum Mode { Menu, Lobby, Round }

        static void Release(SoundInstance instance)
        {
            if (instance == null)
                return;
            instance.Stop();
            instance.Dispose();
        }
    }
}
