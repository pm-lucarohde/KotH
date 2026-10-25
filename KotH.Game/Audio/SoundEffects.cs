using System;
using System.Collections.Generic;
using KotH.Client.Player;
using KotH.Shared.Player;
using Stride.Audio;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Media;

namespace KotH.Client.Audio
{
    /// <summary>
    /// The sounds besides the music: the ambient, looping quietly from the
    /// start (menu and game alike), and what the figures do, placed where they
    /// are shown (PlayerVisualSystem.Sounded) and heard from the camera, so the
    /// own ones sound near and the others fade with distance. The files come
    /// from Tools/audio/sfx.py, all equally loud: the volumes here are the mix.
    /// Presentation only.
    /// </summary>
    [DataContract("KotH.SoundEffects")]
    public sealed class SoundEffects : SyncScript
    {
        /// <userdoc>Läuft von Anfang an in Schleife, im Menü wie im Spiel.</userdoc>
        [Display("Ambient", "Umgebung")] public Sound Ambient;

        /// <userdoc>Deutlich leiser als die Musik (0,2).</userdoc>
        [Display("Lautstärke Ambient", "Umgebung")] public float AmbientVolume = 0.0125f;

        [Display("Schritt", "Figuren")] public Sound Step;
        [Display("Lautstärke Schritt", "Figuren")] public float StepVolume = 0.264f;

        [Display("Sprung", "Figuren")] public Sound Jump;
        [Display("Lautstärke Sprung", "Figuren")] public float JumpVolume = 0.16f;

        /// <userdoc>Beim Doppelsprung, wenn die Sporen stieben.</userdoc>
        [Display("Doppelsprung", "Figuren")] public Sound DoubleJump;
        [Display("Lautstärke Doppelsprung", "Figuren")] public float DoubleJumpVolume = 0.28f;

        /// <userdoc>Bei der Landung nach einem tiefen Fall; nach kleineren Sprüngen klingt ein Schritt.</userdoc>
        [Display("Aufprall", "Figuren")] public Sound Fall;
        [Display("Lautstärke Aufprall", "Figuren")] public float FallVolume = 0.45f;

        /// <userdoc>Ab dieser Fallhöhe (Meter vom höchsten Punkt) klingt der Aufprall. Ein Doppelsprung auf ebenem Boden erreicht etwa 2,7 m.</userdoc>
        [Display("Aufprall ab (m)", "Figuren")] public float FallHeight = 3.5f;

        /// <userdoc>Der Stab saust bei jedem Schlag, ob er trifft oder nicht.</userdoc>
        [Display("Schlag", "Figuren")] public Sound Swing;
        [Display("Lautstärke Schlag", "Figuren")] public float SwingVolume = 0.14f;

        /// <userdoc>Einer davon zufällig, wenn eine andere Figur getroffen wird.</userdoc>
        [Display("Treffer", "Figuren")] public List<Sound> Hits = new List<Sound>();
        [Display("Lautstärke Treffer", "Figuren")] public float HitVolume = 0.5f;

        /// <userdoc>Wenn man selbst getroffen wird.</userdoc>
        [Display("Selbst getroffen", "Figuren")] public Sound Hurt;
        [Display("Lautstärke selbst getroffen", "Figuren")] public float HurtVolume = 0.5f;

        /// <userdoc>Schritte und Schläge klingen je um bis zu diesen Anteil höher oder tiefer, damit sie sich nicht gleich anhören.</userdoc>
        [Display("Tonhöhe streut", "Figuren")] public float PitchSpread = 0.05f;

        /// <summary>Test aid: logs every sound the figures make.</summary>
        [DataMemberIgnore] public bool Trace { get; set; }

        /// <summary>Test aid: every volume here times this, 0 silent.</summary>
        [DataMemberIgnore] public float Master { get; set; } = 1f;

        // Instances of every sound are kept and reused: a new one per step
        // would load the audio thread with creations and disposals.
        const int Voices = 6;

        readonly Dictionary<Sound, List<SoundInstance>> voices = new Dictionary<Sound, List<SoundInstance>>();
        readonly AudioEmitter emitter = new AudioEmitter();
        readonly Random random = new Random();
        PlayerVisualSystem figures;
        PlayerCamera camera;
        AudioListener listener;
        SoundInstance ambient;
        bool failed;

        public override void Start()
        {
            foreach (Entity e in Entity.Scene.Entities)
            {
                figures ??= e.Get<PlayerVisualSystem>();
                camera ??= e.Get<PlayerCamera>();
            }
            if (figures != null)
                figures.Sounded += OnSounded;
            Guard(() =>
            {
                listener = Audio.AudioEngine.DefaultListener;
                if (Ambient == null)
                    return;
                ambient = Ambient.CreateInstance();
                ambient.IsLooping = true;
                ambient.Volume = AmbientVolume * Master;
                ambient.Play();
            });
        }

        public override void Update()
        {
            if (!failed && ambient != null)
                ambient.Volume = AmbientVolume * Master;
        }

        public override void Cancel()
        {
            if (figures != null)
                figures.Sounded -= OnSounded;
            ambient?.Stop();
            ambient?.Dispose();
            foreach (List<SoundInstance> list in voices.Values)
                foreach (SoundInstance instance in list)
                {
                    instance.Stop();
                    instance.Dispose();
                }
            voices.Clear();
        }

        void OnSounded(FigureSound what, PlayerMotor motor, Vector3 at, float amount)
        {
            switch (what)
            {
                case FigureSound.Step:
                    Play(Step, StepVolume * amount, at, true, what);
                    break;
                case FigureSound.Jump:
                    Play(Jump, JumpVolume, at, false, what);
                    break;
                case FigureSound.DoubleJump:
                    Play(DoubleJump, DoubleJumpVolume, at, false, what);
                    break;
                case FigureSound.Land:
                    if (amount >= FallHeight)
                        Play(Fall, FallVolume, at, false, what);
                    else
                        Play(Step, StepVolume, at, true, what);
                    break;
                case FigureSound.Swing:
                    Play(Swing, SwingVolume, at, true, what);
                    break;
                case FigureSound.Hit:
                    if (motor != null && motor == camera?.Motor)
                        Play(Hurt, HurtVolume, at, false, what);
                    else if (Hits.Count > 0)
                        Play(Hits[random.Next(Hits.Count)], HitVolume, at, false, what);
                    break;
            }
        }

        void Play(Sound sound, float volume, Vector3 at, bool vary, FigureSound what)
        {
            if (failed || sound == null || listener == null)
                return;
            if (Trace)
                Console.WriteLine($"[ton] {what} bei {at.X:F1} {at.Y:F1} {at.Z:F1}, vom Ohr {FromEar(at).X:F1} {FromEar(at).Y:F1} {FromEar(at).Z:F1}, Lautstärke {volume:F2}");
            Guard(() =>
            {
                SoundInstance voice = Voice(sound);
                if (voice == null)
                    return;
                emitter.Position = FromEar(at);
                voice.Volume = volume * Master;
                voice.Pitch = vary ? 1f + PitchSpread * (2f * (float)random.NextDouble() - 1f) : 1f;
                voice.Apply3D(emitter);
                voice.Play();
            });
        }

        /// <summary>
        /// The point as heard from the own eye, also in third person (Luca: the
        /// own sounds as loud as in first person), turned with the view. Stride
        /// moves a listener only for an AudioListenerComponent (internally), so
        /// the default one stays at the origin facing +Z with +Y up: the point
        /// goes there in the view's axes, right to +X, up to +Y, ahead to +Z.
        /// </summary>
        Vector3 FromEar(Vector3 at)
        {
            if (camera == null)
                return Vector3.Zero;
            Matrix view = camera.Entity.Transform.WorldMatrix;
            Vector3 ear = camera.Motor != null ? ViewMath.Eye(camera.Motor, TickInterpolation.Alpha) : view.TranslationVector;
            Vector3 d = at - ear;
            return new Vector3(Vector3.Dot(d, Vector3.Normalize(view.Right)), Vector3.Dot(d, Vector3.Normalize(view.Up)),
                               Vector3.Dot(d, Vector3.Normalize(view.Forward)));
        }

        /// <summary>A stopped instance of the sound, a new one while there are fewer than Voices; null when all play.</summary>
        SoundInstance Voice(Sound sound)
        {
            if (!voices.TryGetValue(sound, out List<SoundInstance> list))
                voices[sound] = list = new List<SoundInstance>();
            foreach (SoundInstance instance in list)
                if (instance.PlayState == PlayState.Stopped)
                    return instance;
            if (list.Count >= Voices)
                return null;
            SoundInstance created = sound.CreateInstance(listener);
            list.Add(created);
            return created;
        }

        /// <summary>No audio device or a broken asset: silent from then on, never a stop of the game.</summary>
        void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failed = true;
                Log.Warning($"Geräusche fallen aus: {e.Message}");
            }
        }
    }
}
