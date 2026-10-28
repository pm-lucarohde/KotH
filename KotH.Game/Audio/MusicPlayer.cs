using System;
using System.Collections.Generic;
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

        [Display("Volume", "Music")] public float Volume = 0.1224f;

        /// <userdoc>Scales music, ambience and sounds together, keeping their balance. 2.43 lifts everything until a hit at one's own ear on a music peak reaches about −0.3 dBFS at most (music about −22 LUFS).</userdoc>
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
        bool failed;
        readonly List<string> history = new List<string>();
        float master = -1f;

        public override void Update()
        {
            if (failed)
                return;
            try
            {
                // The listener's gain: one factor over every sound, allowed above 1.
                if (master != MasterVolume)
                    Audio.AudioEngine.MasterVolume = master = MasterVolume;
                // Joining, the round comes with the host's map: until then it is the lobby.
                if (!InMenu && Match == null && ConfirmedRound == null)
                    return;
                Step((float)Game.UpdateTime.Elapsed.TotalSeconds);
            }
            catch (Exception e)
            {
                // No audio device or a broken asset: stay silent, never stop the game.
                failed = true;
                Log.Warning($"Music fails: {e.Message}");
                StopAll();
            }
        }

        public override void Cancel() => StopAll();

        /// <summary>For tests: what plays, how it stands and how often the music changed.</summary>
        [DataMemberIgnore]
        public string Readout
            => failed ? "failed"
             : $"{NameOf(playing) ?? "nothing"}, " +
               $"{current?.PlayState.ToString() ?? "-"} for {age:F1} of {length:F1} s, volume {current?.Volume ?? 0f:F2}, history {string.Join(" > ", history)}";

        void Step(float dt)
        {
            age += dt;
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
            if (current != null)
                current.Volume = Volume * fadeIn;
            if (fading != null)
            {
                fading.Volume = Volume * (1f - fadeIn);
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
            instance.Volume = Volume * fadeIn;
            length = sound.TotalLength.TotalSeconds;
            if (TailSeconds > 0.0)
            {
                TimeSpan from = sound.TotalLength - TimeSpan.FromSeconds(Math.Min(TailSeconds, length));
                instance.SetRange(new PlayRange(from, sound.TotalLength - from));
                length -= from.TotalSeconds;
            }
            instance.Play();
            current = instance;
            if (history.Count < 16)
                history.Add(NameOf(sound));
            Log.Info($"Music: {what} ({NameOf(sound)})");
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
