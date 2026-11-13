using System;
using System.Collections.Generic;
using KotH.Client.Player;
using KotH.Client.Settings;
using KotH.Shared.Player;
using Stride.Audio;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Media;

namespace KotH.Client.Audio
{
    /// <summary>The sounds besides the music: the ambience, looping quietly in menu and game, and
    /// what the figures do, placed where they are shown (PlayerVisualSystem.Sounded) and heard
    /// from the camera. The files (Tools/audio/Sfx.cs) are equally loud: the volumes here are the
    /// mix. Presentation only.</summary>
    [DataContract("KotH.SoundEffects")]
    public sealed class SoundEffects : SyncScript
    {
        /// <userdoc>Loops from the start, in the menu and in the game.</userdoc>
        [Display("Ambient", "Ambience")] public Sound Ambient;

        /// <userdoc>Much quieter than the music (0.1224).</userdoc>
        [Display("Ambient volume", "Ambience")] public float AmbientVolume = 0.00765f;

        [Display("Step", "Figures")] public Sound Step;
        [Display("Step volume", "Figures")] public float StepVolume = 0.169f;

        [Display("Jump", "Figures")] public Sound Jump;
        [Display("Jump volume", "Figures")] public float JumpVolume = 0.088f;

        /// <userdoc>On the double jump, when the spores burst.</userdoc>
        [Display("Double jump", "Figures")] public Sound DoubleJump;
        [Display("Double jump volume", "Figures")] public float DoubleJumpVolume = 0.088f;

        /// <userdoc>On landing after a deep fall; after smaller jumps a step sounds.</userdoc>
        [Display("Impact", "Figures")] public Sound Fall;
        [Display("Impact volume", "Figures")] public float FallVolume = 0.176f;

        /// <userdoc>From this fall height (metres from the highest point) the impact sounds. A double jump on flat ground reaches about 2.7 m.</userdoc>
        [Display("Impact from (m)", "Figures")] public float FallHeight = 8.0f;

        /// <userdoc>When a dash starts, the figure diving down from the air.</userdoc>
        [Display("Dash", "Figures")] public Sound Dash;
        [Display("Dash volume", "Figures")] public float DashVolume = 0.11f;

        /// <userdoc>Where a dash strikes the ground (without it: the impact).</userdoc>
        [Display("Dash impact", "Figures")] public Sound DashImpact;
        [Display("Dash impact volume", "Figures")] public float DashImpactVolume = 0.176f;

        /// <userdoc>Once, when the figure goes from sprint into a slide.</userdoc>
        [Display("Slide", "Figures")] public Sound Slide;
        [Display("Slide volume", "Figures")] public float SlideVolume = 0.091f;

        /// <userdoc>The cane whooshes on every swing, hit or not.</userdoc>
        [Display("Swing", "Figures")] public Sound Swing;
        [Display("Swing volume", "Figures")] public float SwingVolume = 0.077f;

        /// <userdoc>Every spore the blaster fires.</userdoc>
        [Display("Blaster shot", "Figures")] public Sound Shot;
        [Display("Blaster shot volume", "Figures")] public float ShotVolume = 0.132f;

        /// <userdoc>One of these at random whenever a hand grabs a ladder's next rung.</userdoc>
        [Display("Climb grabs", "Figures")] public List<Sound> Climbs = new List<Sound>();
        [Display("Climb volume", "Figures")] public float ClimbVolume = 0.09f;

        /// <userdoc>One of these at random when a trampoline throws a figure back up.</userdoc>
        [Display("Trampolines", "Figures")] public List<Sound> Bounces = new List<Sound>();
        [Display("Trampoline volume", "Figures")] public float BounceVolume = 0.1f;

        /// <userdoc>One of these at random when another figure is hit.</userdoc>
        [Display("Hits", "Figures")] public List<Sound> Hits = new List<Sound>();
        [Display("Hit volume", "Figures")] public float HitVolume = 0.71f;

        /// <userdoc>One of these at random for a step in water (Pyramid's pond).</userdoc>
        [Display("Wading steps", "Figures")] public List<Sound> Wades = new List<Sound>();
        [Display("Wading volume", "Figures")] public float WadeVolume = 0.15f;

        /// <userdoc>Landing in water.</userdoc>
        [Display("Splash", "Figures")] public Sound Splash;
        [Display("Splash volume", "Figures")] public float SplashVolume = 0.16f;

        /// <userdoc>When one is hit oneself.</userdoc>
        [Display("Hurt", "Figures")] public Sound Hurt;
        [Display("Hurt volume", "Figures")] public float HurtVolume = 0.71f;

        /// <userdoc>Steps and swings sound up to this share higher or lower, so they do not sound the same.</userdoc>
        [Display("Pitch spread", "Figures")] public float PitchSpread = 0.05f;

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
        readonly SoundWatch watch = new SoundWatch();
        // A failure silences the sounds for two seconds, never for good.
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double retryAt;

        public override void Start()
        {
            foreach (Entity e in Entity.Scene.Entities)
            {
                figures ??= e.Get<PlayerVisualSystem>();
                camera ??= e.Get<PlayerCamera>();
            }
            if (figures != null)
                figures.Sounded += OnSounded;
        }

        public override void Update()
        {
            if (failed && clock.Elapsed.TotalSeconds >= retryAt)
                failed = false;
            // The ambience after the first frame (its stream start waits on the thread pool the compiles fill).
            if (failed || Game.DrawTime.FrameCount < 1 || Audio.AudioEngine is not { State: not AudioEngineState.Invalidated })
                return;
            listener ??= Audio.AudioEngine.DefaultListener;
            if (Ambient == null)
                return;
            if (ambient == null)
                StartAmbience();
            else if (watch.Silent(ambient, out string why))
            {
                StartLog.Warn($"ambience silent: {why}; started again");
                StartAmbience();
            }
            else
                ambient.Volume = AmbientVolume * Master * PlayerSettings.Gain(ClientSettings.Applied.Ambient);
        }

        void StartAmbience() => Guard(() =>
        {
            SoundInstance old = ambient;
            ambient = null;
            old?.Stop();
            old?.Dispose();
            ambient = Ambient.CreateInstance();
            ambient.IsLooping = true;
            ambient.Volume = AmbientVolume * Master * PlayerSettings.Gain(ClientSettings.Applied.Ambient);
            ambient.Play();
            watch.Started();
        });

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
                case FigureSound.Dash:
                    Play(Dash, DashVolume, at, false, what);
                    break;
                case FigureSound.Slam:
                    if (DashImpact != null)
                        Play(DashImpact, DashImpactVolume, at, false, what);
                    else
                        Play(Fall, FallVolume, at, false, what);
                    break;
                case FigureSound.Slide:
                    Play(Slide, SlideVolume, at, true, what);
                    break;
                case FigureSound.Swing:
                    Play(Swing, SwingVolume, at, true, what);
                    break;
                case FigureSound.Shot:
                    Play(Shot, ShotVolume, at, true, what);
                    break;
                case FigureSound.Climb:
                    if (Climbs.Count > 0)
                        Play(Climbs[random.Next(Climbs.Count)], ClimbVolume, at, true, what);
                    break;
                case FigureSound.Bounce:
                    if (Bounces.Count > 0)
                        Play(Bounces[random.Next(Bounces.Count)], BounceVolume, at, true, what);
                    break;
                case FigureSound.Wade:
                    if (Wades.Count > 0)
                        Play(Wades[random.Next(Wades.Count)], WadeVolume * amount, at, true, what);
                    else
                        Play(Step, StepVolume * amount, at, true, what);
                    break;
                case FigureSound.Splash:
                    Play(Splash ?? Step, Splash != null ? SplashVolume : StepVolume, at, true, what);
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
                Console.WriteLine($"[sound] {what} at {at.X:F1} {at.Y:F1} {at.Z:F1}, from ear {FromEar(camera, at).X:F1} {FromEar(camera, at).Y:F1} {FromEar(camera, at).Z:F1}, volume {volume:F2}");
            Guard(() =>
            {
                SoundInstance voice = Voice(sound);
                if (voice == null)
                    return;
                emitter.Position = FromEar(camera, at);
                voice.Volume = volume * Master * PlayerSettings.Gain(ClientSettings.Applied.Effects);
                voice.Pitch = vary ? 1f + PitchSpread * (2f * (float)random.NextDouble() - 1f) : 1f;
                voice.Apply3D(emitter);
                voice.Play();
            });
        }

        /// <summary>The point as heard from the own eye, also in third person (Luca), turned with the
        /// view. Stride moves a listener only for an AudioListenerComponent, so the default one stays
        /// at the origin facing +Z: the point goes there in the view's axes (right +X, up +Y, ahead +Z).</summary>
        public static Vector3 FromEar(PlayerCamera camera, Vector3 at)
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

        /// <summary>A broken asset or device: silent for two seconds, never a stop of the game.</summary>
        void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failed = true;
                retryAt = clock.Elapsed.TotalSeconds + 2.0;
                Log.Warning($"Sounds fail: {e.Message}");
                StartLog.Warn($"sounds fail: {e}");
            }
        }
    }
}
