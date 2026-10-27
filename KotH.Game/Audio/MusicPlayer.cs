using System;
using System.Collections.Generic;
using KotH.Shared.Match;
using Stride.Audio;
using Stride.Core;
using Stride.Engine;
using Stride.Media;

namespace KotH.Client.Audio
{
    /// <summary>
    /// The music: one looping piece in the main menu, one before the round
    /// (lobby, countdown and the pause after a win), a fixed piece when a round
    /// starts and then the playlist in shuffled order; every change crossfades. Follows the confirmed
    /// round when networked, so no rollback flips it. Presentation only.
    /// </summary>
    [DataContract("KotH.MusicPlayer")]
    public sealed class MusicPlayer : SyncScript
    {
        /// <userdoc>Läuft in Schleife, solange das Hauptmenü offen ist.</userdoc>
        [Display("Hauptmenü", "Musik")] public Sound Menu;

        /// <userdoc>Läuft in Schleife, solange kein Spiel läuft: Warten auf Spieler, Countdown und die Pause nach dem Sieg.</userdoc>
        [Display("Vor dem Spiel", "Musik")] public Sound Lobby;

        /// <userdoc>Läuft einmal, sobald eine Runde beginnt.</userdoc>
        [Display("Spielbeginn", "Musik")] public Sound RoundStart;

        /// <userdoc>Nach dem Spielbeginn-Track zufällig nacheinander: jeder einmal, bevor einer wiederkommt.</userdoc>
        [Display("Danach zufällig", "Musik")] public List<Sound> Playlist = new List<Sound>();

        [Display("Lautstärke", "Musik")] public float Volume = 0.136f;

        /// <userdoc>Verstärkt Musik, Umgebung und Geräusche gemeinsam, ihre Lautstärken untereinander bleiben. 2,43 hebt alles so weit an, dass ein Treffer am eigenen Ohr auch auf einer Spitze der Musik höchstens etwa −0,3 dBFS erreicht (Musik etwa −22 LUFS).</userdoc>
        [Display("Gesamtlautstärke", "Alles")] public float MasterVolume = 2.43f;

        /// <userdoc>So lange blendet ein Track in den nächsten über (Sekunden), auch wenn einer ausläuft: der nächste beginnt so lange vor dessen Ende.</userdoc>
        [Display("Überblenden", "Musik")] public float FadeSeconds = 3.5f;

        [DataMemberIgnore] public HillMatch Match { get; set; }

        /// <summary>The main menu is open: its piece plays, whatever the round does.</summary>
        [DataMemberIgnore] public bool InMenu { get; set; }

        /// <summary>Networked: the round as the host confirmed it, null before the first.</summary>
        [DataMemberIgnore] public Func<MatchState?> ConfirmedRound { get; set; }

        /// <summary>Test aid: start every track this many seconds before its end.</summary>
        [DataMemberIgnore] public double TailSeconds { get; set; }

        // A streamed instance keeps reporting Playing after its end, so the
        // player times the piece itself and starts the next one a fade before.
        // Every start gets a new instance: a streamed one played again after
        // Stop replayed old buffers for seconds (the lobby over the second
        // round's start).

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
                Log.Warning($"Musik fällt aus: {e.Message}");
                StopAll();
            }
        }

        public override void Cancel() => StopAll();

        /// <summary>For tests: what plays, how it stands and how often the music changed.</summary>
        [DataMemberIgnore]
        public string Readout
            => failed ? "ausgefallen"
             : $"{NameOf(playing) ?? "nichts"}, " +
               $"{current?.PlayState.ToString() ?? "-"} seit {age:F1} von {length:F1} s, Lautstärke {current?.Volume ?? 0f:F2}, Verlauf {string.Join(" > ", history)}";

        void Step(float dt)
        {
            age += dt;
            Mode now = InMenu ? Mode.Menu : Phase() == MatchPhase.Running ? Mode.Round : Mode.Lobby;
            if (now != mode)
            {
                mode = now;
                if (now == Mode.Menu)
                    Switch(Menu, "Hauptmenü");
                else
                    Switch(now == Mode.Round ? RoundStart : Lobby, now == Mode.Round ? "Spielbeginn" : "Vor dem Spiel");
            }
            else if (now == Mode.Round && current != null && !current.IsLooping && age >= length - Math.Min(FadeSeconds, length * 0.5))
                Switch(Draw(), "Zufall");

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
            Log.Info($"Musik: {what} ({NameOf(sound)})");
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
