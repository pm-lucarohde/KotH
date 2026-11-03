using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using KotH.Client;
using KotH.Client.Audio;
using KotH.Client.Hud;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Client.Settings;
using KotH.Client.Steam;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Core.Threading;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;

#if !WINDOWS
StdCxxPreload.Run();
#endif

// Logs with decimal points, whatever the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// Six render threads instead of all 24: Stride's workers spin after every
// parallel loop, and on this scene fewer of them are faster and far lighter
// on the CPU (measured 20.10.2026, Luca's choice). The variable still wins.
if (Environment.GetEnvironmentVariable("STRIDE_MAX_PARALLELISM") == null)
    Dispatcher.MaxDegreeOfParallelism = 6;

// Before the options are read: the player's name comes from Steam.
SteamClient.Start();
using var game = new KotHGame();
game.Run();
SteamClient.Shutdown();

/// <summary>The game: borderless fullscreen, F11 toggles the window. Plus test aids (KOTH_*
/// below, times from the end of the load screen): KOTH_SHOT=path saves the back buffer as PNG
/// after KOTH_SHOT_SECONDS with the F3 readout and quits; taken inside the game on purpose, a
/// desktop screenshot would capture whatever else is on the screen.</summary>
sealed class KotHGame : Game
{
    readonly string shotPath = Environment.GetEnvironmentVariable("KOTH_SHOT");
    readonly double shotSeconds = Number("KOTH_SHOT_SECONDS") ?? 3.0;
    readonly double? shotYaw = Number("KOTH_SHOT_YAW"); // view held there, with _PITCH and _FOV
    readonly double shotSpin = Number("KOTH_SHOT_SPIN") ?? 0.0; // turns the view steadily, deg/s
    readonly double shotPitch = Number("KOTH_SHOT_PITCH") ?? 0.0;
    readonly double? shotFov = Number("KOTH_SHOT_FOV");
    readonly bool shotThird = Environment.GetEnvironmentVariable("KOTH_SHOT_THIRD") == "1"; // third person
    Vector3? shotAt = Point("KOTH_SHOT_AT"); // body there (Stride metres), again on every placing
    readonly double? shotFace = Number("KOTH_SHOT_FACE"); // turned this way (deg) when placed
    int shotPlaced = -1;
    bool shotSwing = Environment.GetEnvironmentVariable("KOTH_SHOT_SWING") == "1"; // swings 1 s before, reports the hit
    bool shotShake = Environment.GetEnvironmentVariable("KOTH_SHOT_SHAKE") == "1"; // takes a hit, reports the shake
    bool shotBlink = Environment.GetEnvironmentVariable("KOTH_SHOT_BLINK") == "1"; // every figure shows shut lids
    double? shotHit = Number("KOTH_SHOT_HIT", 0); // deg[,s]: hits every figure from deg (0 front, 90 right)
    readonly double shotHitLead = Number("KOTH_SHOT_HIT", 1) ?? 0.1; // s before the shot
    string shotMove = Environment.GetEnvironmentVariable("KOTH_SHOT_MOVE"); // x,y[,sprint] held (D and W +1)
    // KOTH_SHOT_APPLY=s:file.toml[;s:file.toml]: Apply on the Settings page with that file's settings at s.
    readonly Queue<(double At, string File)> shotApplies = new Queue<(double, string)>(
        (Environment.GetEnvironmentVariable("KOTH_SHOT_APPLY") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (double.Parse(p[..p.IndexOf(':')], CultureInfo.InvariantCulture), p[(p.IndexOf(':') + 1)..])));
    double? shotCrouch = Number("KOTH_SHOT_CROUCH"); // crouch held from s (out of a sprint: slide)
    double? shotDash = Number("KOTH_SHOT_DASH", 0); // s[,m]: crouch tapped at s (a dash from 1.5 m up), lifted m first
    readonly double shotDashLift = Number("KOTH_SHOT_DASH", 1) ?? 0.0;
    bool shotDashLifted;
    readonly double shotVideo = Number("KOTH_SHOT_VIDEO") ?? 0.0; // last s as raw 30 fps frames, path.rgba
    FileStream video;
    double videoAt = double.NaN;
    // KOTH_SHOT_TURN=deg,s[,orbit] turns the view at s (third person: the camera circles by orbit),
    // KOTH_SHOT_JUMP=s[,s2] jumps (s2: double jump), KOTH_SHOT_STRIKE=s[,s2] swings every cane.
    readonly double[] shotTurn = Environment.GetEnvironmentVariable("KOTH_SHOT_TURN")?.Split(',')
        .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    readonly Queue<double> shotJumps = new Queue<double>(
        (Environment.GetEnvironmentVariable("KOTH_SHOT_JUMP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)));
    // KOTH_SHOT_BLASTER=s draws the blaster at s, KOTH_SHOT_FIRE=s[,s2] presses the trigger (or swings).
    double? shotBlaster = Number("KOTH_SHOT_BLASTER");
    readonly Queue<double> shotFires = new Queue<double>(
        (Environment.GetEnvironmentVariable("KOTH_SHOT_FIRE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)));
    readonly Queue<double> shotStrikes = new Queue<double>(
        (Environment.GetEnvironmentVariable("KOTH_SHOT_STRIKE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)));
    readonly string frameLogPath = Environment.GetEnvironmentVariable("KOTH_FRAMELOG"); // frame times (ms) at the end
    readonly string posLogPath = Environment.GetEnvironmentVariable("KOTH_POSLOG"); // own body per tick, F8 marks
    StreamWriter posLog;
    int posTick = int.MinValue;
    readonly double? musicVolume = Number("KOTH_MUSIC_VOLUME"); // 0..1; 0 mutes the sounds too
    readonly double? musicTail = Number("KOTH_MUSIC_TAIL"); // tracks start s before their end
    readonly double? sfxVolume = Number("KOTH_SFX_VOLUME"); // 0..1 for ambience and figures
    readonly bool soundLog = Environment.GetEnvironmentVariable("KOTH_SOUNDLOG") == "1"; // logs every sound
    bool sfxSet;
    readonly bool headless = Environment.GetEnvironmentVariable("KOTH_HEADLESS") == "1"; // no window, renders KOTH_SIZE
    bool musicSet;
    Int2 windowedSize;
    bool confined;

    // Fullscreen before loading, see Initialize; the size asked for then is
    // where F11 returns to. A fixed KOTH_SIZE stays a window.
    readonly bool startFullscreen = Environment.GetEnvironmentVariable("KOTH_SIZE") == null &&
                                    Environment.GetEnvironmentVariable("KOTH_HEADLESS") != "1";
    int restoreFrames;
    readonly KotH.Client.Settings.SettingsApplier settings;
    WindowMode? windowShown;
    Int2 sizeShown;
    int windowVersion;

    // Ten minutes at 2000 FPS, allocated once so logging never allocates.
    float[] frameLog;
    int frameCount;

    public KotHGame()
    {
        // Warnings and errors reach the terminal in Release too; otherwise
        // a script failing in Start leaves nothing but a black window.
        ConsoleLogMode = ConsoleLogMode.Always;
        ConsoleLogLevel = LogMessageType.Warning;

        if (frameLogPath != null)
            frameLog = new float[600 * 2000];

        // DLSS needs Vulkan extensions Stride does not ask for: hooked before the device exists.
        if (Environment.GetEnvironmentVariable("KOTH_DLSS") != "0")
            KotH.Client.Rendering.Dlss.Dlss.Prepare();

        // A hidden window never has the focus; a networked game must keep
        // its clock with the other window in front.
        MakeWindowVisibleOnRun = !headless;
        if (headless)
        {
            // The hidden window counts as minimised: no 15 fps cap, still drawn.
            DrawWhileMinimized = true;
            MinimizedMinimumUpdateRate.MinimumElapsedTime = TimeSpan.Zero;
        }
        // Every game may become a host: the pause menu leads back to the main menu.
        TreatNotFocusedLikeMinimized = false;
        InactiveSleepTime = TimeSpan.Zero;

        // Frame cap and V-Sync from the settings (off by Luca's choice), --fps, KOTH_FPS or KOTH_VSYNC.
        settings = new KotH.Client.Settings.SettingsApplier(this, headless);

        // The sound device of the settings; OpenAL opens it when the audio starts, so a new one needs a restart.
        if (KotH.Client.Settings.ClientSettings.Applied.OutputDevice is { Length: > 0 } device)
            Audio.RequestedAudioDevice = new Stride.Audio.AudioDevice { Name = device };
    }

    protected override void PrepareContext()
    {
        base.PrepareContext();

        // No splash screen, even if the settings name one again. With a splash
        // Stride loads the scene on worker threads, and their uploads race the
        // first swapchain resize: vkDeviceWaitIdle runs without the queue lock
        // and about one AOT start in six ended in VK_ERROR_DEVICE_LOST.
        SceneSystem.SplashScreenUrl = null;

        // After the settings are applied, so the override wins over them.
        string[] size = Environment.GetEnvironmentVariable("KOTH_SIZE")?.Split('x');
        if (size is { Length: 2 } && int.TryParse(size[0], out int width) && int.TryParse(size[1], out int height))
        {
            GraphicsDeviceManager.PreferredBackBufferWidth = width;
            GraphicsDeviceManager.PreferredBackBufferHeight = height;
        }
    }

    /// <summary>Waits for the window manager to show and focus the window (two seconds at most),
    /// then goes fullscreen and captures the mouse. Waiting for the first frame after loading let
    /// the pointer leave for the other screen, and a grab only holds in a window covering the screen.</summary>
    protected override void Initialize()
    {
        base.Initialize();
        KotH.Client.Rendering.EffectRecorder.Attach(EffectSystem);
        if (headless)
            return;
        if (Window.NativeWindow?.NativeWindow is not Stride.Graphics.SDL.Window sdl)
            return;
        KotH.Client.Settings.DisplayModes.Window = sdl.SdlHandle;

        // Pumping only moves the window's events into SDL's queue; the game
        // still reads every one of them later.
        PumpUntil(() => SdlGrab.SDL_GetKeyboardFocus() == sdl.SdlHandle);

        if (LaunchOptions.Current.Display is { } display)
            MoveToDisplay(sdl.SdlHandle, display);

        if (startFullscreen)
        {
            // The window as the settings say. The size the window manager gave the new window is
            // where F11 returns to. Stride applies a switch only in its loop, after loading; the
            // window goes now, the device follows.
            SdlGrab.SDL_GetWindowSize(sdl.SdlHandle, out windowedSize.X, out windowedSize.Y);
            // Stride's windows keep their size by default: no frame to drag, no maximise button.
            Window.AllowUserResizing = true;
            ApplyWindow();
            if (windowShown != WindowMode.Windowed)
                PumpUntil(() => CoversDisplay(sdl.SdlHandle));
            if (LaunchOptions.Current.Display != null)
            {
                SdlGrab.SDL_GetWindowPosition(sdl.SdlHandle, out int fx, out int fy);
                Console.WriteLine($"[monitor] fullscreen at {fx},{fy} (monitor {LaunchOptions.Current.Display} from the left wanted)");
            }
        }

        // The main menu wants the pointer; SimulationDriver grabs it when a game starts.
        if (LaunchOptions.Current.ShowsMenu)
            return;
        SdlGrab.SDL_SetRelativeMouseMode(1);
        SdlGrab.SDL_SetWindowGrab(sdl.SdlHandle, 1);
    }

    /// <summary>
    /// KOTH_HEADLESS: the frames go into a texture instead of the hidden
    /// window's swapchain, in the formats it would have; shots read it like the back buffer.
    /// </summary>
    void RenderOffscreen()
    {
        GraphicsDeviceManager manager = GraphicsDeviceManager;
        string[] size = (Environment.GetEnvironmentVariable("KOTH_SIZE") ?? "2560x1440").Split('x');
        PixelFormat format = GraphicsDevice.ColorSpace == ColorSpace.Linear
            ? manager.PreferredBackBufferFormat.ToSRgb() : manager.PreferredBackBufferFormat;
        Texture target = Texture.New2D(GraphicsDevice, int.Parse(size[0]), int.Parse(size[1]), format,
                                       TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        GraphicsDevice.Presenter = new RenderTargetGraphicsPresenter(GraphicsDevice, target, manager.PreferredDepthStencilFormat);
        Console.WriteLine($"[headless] {target.Width}x{target.Height} {target.ViewFormat}");
    }

    /// <summary>
    /// Puts the window on a monitor counted from the left, 0 the leftmost,
    /// before it goes fullscreen there. SDL's own numbering follows no order.
    /// </summary>
    static void MoveToDisplay(nint window, int fromLeft)
    {
        int count = SdlGrab.SDL_GetNumVideoDisplays();
        var bounds = new System.Collections.Generic.List<SdlGrab.Rect>();
        for (int i = 0; i < count; i++)
            if (SdlGrab.SDL_GetDisplayBounds(i, out SdlGrab.Rect r) == 0)
                bounds.Add(r);
        if (bounds.Count == 0)
            return;
        bounds.Sort((a, b) => a.X.CompareTo(b.X));
        SdlGrab.Rect target = bounds[Math.Clamp(fromLeft, 0, bounds.Count - 1)];
        // The window manager hands out the new window maximised, and a
        // maximised window does not move.
        SdlGrab.SDL_RestoreWindow(window);
        SdlGrab.SDL_SetWindowPosition(window, target.X + 40, target.Y + 40);
        PumpUntil(() =>
        {
            SdlGrab.SDL_GetWindowPosition(window, out int x, out int y);
            return x >= target.X && x < target.X + target.W;
        });
    }

    static void PumpUntil(Func<bool> done)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && wait.ElapsedMilliseconds < 2000)
        {
            SdlGrab.SDL_PumpEvents();
            System.Threading.Thread.Sleep(5);
        }
    }

    static bool CoversDisplay(nint window)
    {
        SdlGrab.SDL_GetWindowSize(window, out int width, out int height);
        return SdlGrab.SDL_GetDisplayBounds(SdlGrab.SDL_GetWindowDisplayIndex(window), out SdlGrab.Rect display) == 0 &&
               width == display.W && height == display.H;
    }

    // Stride makes the device after Initialize: NGX starts on it here.
    protected override void BeginRun()
    {
        base.BeginRun();
        KotH.Client.Rendering.Dlss.Dlss.Start(GraphicsDevice);
    }

    protected override void EndRun()
    {
        KotH.Client.Rendering.Dlss.Dlss.Shutdown();
        base.EndRun();

        if (frameLog == null)
            return;
        using var writer = new StreamWriter(frameLogPath);
        for (int i = 0; i < frameCount; i++)
            writer.WriteLine(frameLog[i].ToString("R", CultureInfo.InvariantCulture));
        Console.WriteLine($"[frames] {frameCount} frames to {frameLogPath}");
    }

    protected override void Update(GameTime gameTime)
    {
        // Steam's callbacks before the scripts, which read lobbies and sessions.
        SteamClient.RunFrame();
        settings.Update();
        if (windowVersion != ClientSettings.Version)
        {
            windowVersion = ClientSettings.Version;
            ApplyWindow();
        }
        // Before the scripts run, so the first piece already starts with these.
        if (!musicSet && (musicVolume.HasValue || musicTail.HasValue) && Find<MusicPlayer>() is { } music)
        {
            musicSet = true;
            music.Volume = (float)(musicVolume ?? music.Volume);
            music.TailSeconds = musicTail ?? 0.0;
        }
        if (!sfxSet && Find<SoundEffects>() is { } sounds)
        {
            sfxSet = true;
            sounds.Master = (float)(sfxVolume ?? (musicVolume == 0.0 ? 0.0 : 1.0));
            sounds.Trace = soundLog;
        }

        // Not in Initialize: with the window hidden the device is not ready there.
        if (headless && GraphicsDevice.Presenter is not RenderTargetGraphicsPresenter)
            RenderOffscreen();

        base.Update(gameTime);

        if (KeyPresses.Pressed(Input, Keys.F11))
            ToggleFullscreen();
        RestoreWindowSize();
        ConfinePointer();
        LogPosition();

        if (shotPath == null)
            return;

        if (Find<PerformanceOverlay>() is { } overlay)
            overlay.Visible = true;
        double now = TestSeconds(gameTime);
        while (shotApplies.Count > 0 && now >= shotApplies.Peek().At)
        {
            Console.WriteLine($"[settings] {now:F1} s: {shotApplies.Peek().File}");
            ClientSettings.Apply(SettingsFile.Load(shotApplies.Dequeue().File));
        }
        bool turned = shotTurn != null && now >= shotTurn[1];
        bool orbit = turned && shotThird && shotTurn.Length > 2 && now >= shotTurn[1] + 0.2;
        if ((shotYaw.HasValue || shotTurn != null) && Find<PlayerInputReader>() is { } reader)
            reader.Look((float)((shotYaw ?? 0.0) + shotSpin * Math.Max(now, 0.0) + (turned ? shotTurn[0] : 0.0) + (orbit ? shotTurn[2] : 0.0)), (float)shotPitch);
        if (shotFov.HasValue && Find<CameraComponent>() is { } camera)
            camera.VerticalFieldOfView = (float)shotFov.Value;
        if (Find<PlayerInputReader>() is { } player)
        {
            if (shotThird && (shotTurn == null || now >= shotTurn[1] + 0.2))
                player.ThirdPerson = true;
            if (shotMove != null)
            {
                string[] parts = shotMove.Split(',');
                player.Hold(new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture),
                                        float.Parse(parts[1], CultureInfo.InvariantCulture)),
                            parts.Length > 2 && parts[2] == "sprint");
                shotMove = null;
            }
            // Also after the round has put everyone at its start (MountDestiny: at random).
            if (shotAt.HasValue && player.PlacedTick != shotPlaced &&
                player.Entity.Get<KotH.Shared.Player.PlayerMotor>() is { } motor)
            {
                if (shotFace.HasValue)
                    player.PlaceAt(shotAt.Value, (float)shotFace.Value);
                else
                    motor.Teleport(shotAt.Value);
                shotPlaced = player.PlacedTick;
            }
            if (shotCrouch.HasValue && TestSeconds(gameTime) >= shotCrouch.Value)
            {
                player.HoldCrouch();
                shotCrouch = null;
            }
            if (shotDash.HasValue && shotDashLift > 0 && !shotDashLifted && TestSeconds(gameTime) >= shotDash.Value - 0.1 &&
                player.Entity.Get<KotH.Shared.Player.PlayerMotor>() is { } lifted)
            {
                lifted.Teleport(lifted.Position + new Vector3(0f, (float)shotDashLift, 0f));
                shotDashLifted = true;
            }
            if (shotDash.HasValue && TestSeconds(gameTime) >= shotDash.Value)
            {
                player.TapCrouch();
                shotDash = null;
            }
            while (shotJumps.Count > 0 && TestSeconds(gameTime) >= shotJumps.Peek())
            {
                player.Jump();
                shotJumps.Dequeue();
            }
            if (shotBlaster.HasValue && TestSeconds(gameTime) >= shotBlaster.Value)
            {
                player.Blaster = true;
                shotBlaster = null;
            }
            while (shotFires.Count > 0 && TestSeconds(gameTime) >= shotFires.Peek())
            {
                player.Swing();
                shotFires.Dequeue();
            }
            if (shotSwing && TestSeconds(gameTime) >= shotSeconds - 1.0)
            {
                player.Swing();
                shotSwing = false;
            }

            // A hit taken just before the shot, as the camera sees one: a new tick.
            if (shotShake && TestSeconds(gameTime) >= shotSeconds - 0.05 &&
                player.Entity.Get<KotH.Shared.Combat.DamagePercent>() is { } hit)
            {
                hit.LastHitTick++;
                shotShake = false;
            }
        }

        // Every figure hit just before the shot, from one side; a frame early
        // enough even while the start still runs at 10 fps.
        if (shotHit.HasValue && TestSeconds(gameTime) >= shotSeconds - shotHitLead &&
            Find<PlayerVisualSystem>() is { } struck)
        {
            struck.HitAll((float)shotHit.Value);
            shotHit = null;
        }

        // Every figure swings its cane at these times, only shown.
        while (shotStrikes.Count > 0 && TestSeconds(gameTime) >= shotStrikes.Peek() &&
               Find<PlayerVisualSystem>() is { } swinging)
        {
            swinging.SwingAll();
            shotStrikes.Dequeue();
        }

        // Shut at the shot: a third of the blink closes the lid.
        if (shotBlink && Find<PlayerVisualSystem>() is { } visuals &&
            TestSeconds(gameTime) >= shotSeconds - visuals.BlinkSeconds * 0.4)
        {
            visuals.BlinkAll();
            shotBlink = false;
        }
    }

    /// <summary>SDL's relative mode only hides the pointer here (X11 on Wayland): it
    /// never grabs, so the pointer left the fullscreen window for the other screen. While
    /// the game has the mouse, the window grabs it as well.</summary>
    void ConfinePointer()
    {
        bool confine = Input.IsMousePositionLocked && Window.Focused;
        if (confine == confined || Window.NativeWindow?.NativeWindow is not Stride.Graphics.SDL.Window sdl)
            return;
        SdlGrab.SDL_SetWindowGrab(sdl.SdlHandle, confine ? 1 : 0);
        confined = confine;
    }

    /// <summary>F11: from fullscreen (borderless or exclusive, as set) to the window and back;
    /// for this session only, the settings stay.</summary>
    void ToggleFullscreen()
    {
        if (GraphicsDeviceManager.IsFullScreen)
            GoWindowed(windowedSize);
        else
            GoFullscreen(ClientSettings.Applied.Window == WindowMode.Exclusive, new Int2(ClientSettings.Applied.Width, ClientSettings.Applied.Height));
    }

    /// <summary>The Window and Resolution settings, at the start and after an Apply that changed them.
    /// Borderless keeps the screen's size (Resolution there is the scene's, see DlssRenderer). Not with
    /// KOTH_SIZE or headless, which keep their fixed window.</summary>
    void ApplyWindow()
    {
        if (!startFullscreen)
            return;
        PlayerSettings s = ClientSettings.Applied;
        var size = new Int2(s.Width, s.Height);
        if (s.Window == windowShown && (s.Window == WindowMode.Borderless || size == sizeShown))
            return;
        (windowShown, sizeShown) = (s.Window, size);
        if (s.Window == WindowMode.Windowed)
            GoWindowed(size);
        else
            GoFullscreen(s.Window == WindowMode.Exclusive, size);
    }

    /// <summary>Set on the device manager: Stride 4.4's Window.IsFullscreen handler runs the orientation
    /// update instead. SDL is told too: a switch between borderless and exclusive changes no flag Stride
    /// compares, and before the loop Stride applies nothing. Exclusive switches the screen's mode.</summary>
    void GoFullscreen(bool exclusive, Int2 size)
    {
        GraphicsDeviceManager manager = GraphicsDeviceManager;
        if (!manager.IsFullScreen)
        {
            Rectangle bounds = Window.ClientBounds;
            windowedSize = new Int2(bounds.Width, bounds.Height);
        }
        restoreFrames = 0;
        Int2 native = DisplayModes.Native();
        Int2 full = exclusive && size != Int2.Zero ? size : native != Int2.Zero ? native : new Int2(Window.ClientBounds.Width, Window.ClientBounds.Height);
        Window.FullscreenIsBorderlessWindow = !exclusive;
        FollowSizeEvents(!exclusive);
        var sdl = Window.NativeWindow?.NativeWindow as Stride.Graphics.SDL.Window;
        // Exclusive: the mode first (SDL takes it from the window's size otherwise, which the window
        // manager resets while switching), then the device at its size.
        if (exclusive && sdl != null)
        {
            var mode = new SdlGrab.DisplayMode { W = full.X, H = full.Y };
            SdlGrab.SDL_SetWindowDisplayMode(sdl.SdlHandle, ref mode);
            SdlGrab.SDL_SetWindowSize(sdl.SdlHandle, full.X, full.Y);
            SdlGrab.SDL_SetWindowFullscreen(sdl.SdlHandle, SdlGrab.Fullscreen);
        }
        manager.PreferredBackBufferWidth = full.X;
        manager.PreferredBackBufferHeight = full.Y;
        manager.IsFullScreen = true;
        manager.ApplyChanges();
        if (!exclusive && sdl != null)
            SdlGrab.SDL_SetWindowFullscreen(sdl.SdlHandle, SdlGrab.FullscreenDesktop);
    }

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "ClientSizeChanged")]
    static extern ref EventHandler<EventArgs> SizeEvents(GameWindow window);

    EventHandler<EventArgs> managerSizeHandler;

    /// <summary>In exclusive fullscreen the device manager must not follow the window's size: each of its
    /// device changes sets the size again, SDL then sets the screen's mode again and reports a new size,
    /// a swapchain rebuild every frame (8 fps, the pointer kept in the middle). ApplyWindow sizes it there.</summary>
    void FollowSizeEvents(bool follow)
    {
        ref EventHandler<EventArgs> events = ref SizeEvents(Window);
        if (!follow && managerSizeHandler == null && events != null)
            foreach (Delegate d in events.GetInvocationList())
                if (d.Target == GraphicsDeviceManager)
                {
                    managerSizeHandler = (EventHandler<EventArgs>)d;
                    events -= managerSizeHandler;
                }
        if (follow && managerSizeHandler != null)
        {
            events += managerSizeHandler;
            managerSizeHandler = null;
        }
    }

    /// <summary>A window of that size, or maximised for zero (the screen's own size).</summary>
    void GoWindowed(Int2 size)
    {
        GraphicsDeviceManager manager = GraphicsDeviceManager;
        FollowSizeEvents(true);
        bool maximise = size == Int2.Zero;
        if (!maximise)
            windowedSize = size;
        if (manager.IsFullScreen)
        {
            manager.PreferredBackBufferWidth = windowedSize.X;
            manager.PreferredBackBufferHeight = windowedSize.Y;
            manager.IsFullScreen = false;
            manager.ApplyChanges();
            if (Window.NativeWindow?.NativeWindow is Stride.Graphics.SDL.Window sdl)
                SdlGrab.SDL_SetWindowFullscreen(sdl.SdlHandle, 0);
        }
        restoreFrames = maximise ? 0 : 30;
        if (maximise && Window.NativeWindow?.NativeWindow is Stride.Graphics.SDL.Window window)
            SdlGrab.SDL_MaximizeWindow(window.SdlHandle);
        else
            Window.SetSize(windowedSize);
    }

    /// <summary>Stride asks for the old window size while the window is still fullscreen,
    /// and the window manager then brings back the fullscreen size. Asked again once the
    /// window is back, for half a second at most.</summary>
    void RestoreWindowSize()
    {
        if (restoreFrames <= 0)
            return;
        restoreFrames--;

        Rectangle bounds = Window.ClientBounds;
        if (bounds.Width != windowedSize.X || bounds.Height != windowedSize.Y)
            Window.SetSize(windowedSize);
        else
            restoreFrames = 0;
    }

    static double? Number(string variable) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value) ? value : null;

    /// <summary>The index-th of the comma-separated numbers in the variable.</summary>
    static double? Number(string variable, int index)
    {
        string[] parts = Environment.GetEnvironmentVariable(variable)?.Split(',');
        return parts != null && index < parts.Length &&
               double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    static Vector3? Point(string variable)
    {
        string[] parts = Environment.GetEnvironmentVariable(variable)?.Split(',');
        if (parts == null || parts.Length != 3)
            return null;
        var v = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                return null;
        return new Vector3(v[0], v[1], v[2]);
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);

        if (frameLog != null && frameCount < frameLog.Length)
        {
            if (frameCount == 0)
                Console.WriteLine($"[frames] first frame {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms after process start");
            frameLog[frameCount++] = (float)gameTime.Elapsed.TotalMilliseconds;
        }

        if (shotPath != null && shotVideo > 0.0)
            RecordVideo(TestSeconds(gameTime));
        if (shotPath == null || TestSeconds(gameTime) < shotSeconds)
            return;
        video?.Dispose();

        // The back buffer's alpha is whatever the last pass left, low in
        // shadows; a viewer blends that with white. Opaque before saving.
        using (Image image = GraphicsDevice.Presenter.BackBuffer.GetDataAsImage(GraphicsContext.CommandList))
        using (FileStream f = File.Create(shotPath))
        {
            PixelBuffer pixels = image.PixelBuffer[0];
            byte[] data = pixels.GetPixels<byte>();
            for (int i = 3; i < data.Length; i += 4)
                data[i] = 255;
            pixels.SetPixels(data);
            image.Save(f, ImageFileType.Png);
        }

        Console.WriteLine($"[shot] {shotPath} after {TestSeconds(gameTime):F1} s, {gameTime.FramePerSecond:F0} fps, " +
                          $"yaw {Find<PlayerInputReader>()?.Yaw:F2}°");
        if (Find<MusicPlayer>() is { } music)
            Console.WriteLine($"[music] {music.Readout}");
        if (Find<PlayerInputReader>()?.Entity.Get<KotH.Shared.Combat.MeleeAttack>() is { } melee)
            Console.WriteLine($"[swing] {melee.SwingCount} swings, {melee.HitCount} hits, " +
                              $"last {melee.LastHitDistance:F3} m");
        if (Find<PlayerInputReader>() is { } look && Find<PlayerCamera>() is { } view)
        {
            Quaternion still = KotH.Shared.Mathematics.UMath.Euler(look.Pitch, look.Yaw);
            float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(still, view.Entity.Transform.Rotation)));
            Console.WriteLine(string.Format(HudRenderer.Numbers, "[shake] view tilted {0:F2}° against the aim",
                                            2f * MathF.Acos(dot) * 180f / MathF.PI));
        }
        ReportFont();
        Exit();
    }

    /// <summary>
    /// One frame of KOTH_SHOT_VIDEO, written once for every 1/30 s slot it
    /// covers: a slow frame repeats, so the video keeps the game's time.
    /// </summary>
    // The test helpers count from the moment the load screen is gone, as a
    // player's time starts then; -1 while it still covers the screen. With
    // KOTH_SHOT_LOAD=1 it stays, and they count from the first frame.
    double coverLifted = double.NaN;
    readonly bool shotLoad = Environment.GetEnvironmentVariable("KOTH_SHOT_LOAD") == "1";

    double TestSeconds(GameTime time)
    {
        if (double.IsNaN(coverLifted) && (shotLoad || Services.GetService<LoadScreen>() is not { Covers: true }))
            coverLifted = time.Total.TotalSeconds;
        return double.IsNaN(coverLifted) ? -1.0 : time.Total.TotalSeconds - coverLifted;
    }

    void RecordVideo(double now)
    {
        if (double.IsNaN(videoAt))
            videoAt = shotSeconds - shotVideo;
        int slots = 0;
        for (; videoAt <= now; videoAt += 1.0 / 30.0)
            slots++;
        if (slots == 0)
            return;
        using Image image = GraphicsDevice.Presenter.BackBuffer.GetDataAsImage(GraphicsContext.CommandList);
        byte[] data = image.PixelBuffer[0].GetPixels<byte>();
        if (video == null)
        {
            video = File.Create(shotPath + ".rgba");
            Console.WriteLine($"[video] {shotPath}.rgba {image.Description.Width}x{image.Description.Height} {image.Description.Format} 30 fps");
        }
        for (int i = 0; i < slots; i++)
            video.Write(data);
    }

    /// <summary>MeleeProbe's font check, moved here because measuring needs the loaded
    /// font and so a graphics device: a font without glyphs draws nothing while every
    /// other check stays green.</summary>
    void ReportFont()
    {
        DamageLabel label = Find<DamageLabel>();
        if (label == null)
        {
            Console.WriteLine("[font] no DamageLabel in the scene");
            return;
        }

        Vector2 size = label.Measure("1000.0 %");
        Console.WriteLine(string.Format(HudRenderer.Numbers,
            "[font] {0}, \"1000.0 %\" measures {1:F3} x {2:F3} m (must be above zero), " +
            "font size {3:F2} m, current text \"{4}\", label at y {5:F3}",
            label.Font?.FontType, size.X, size.Y, label.FontSize, label.Text, label.Entity.Transform.Position.Y));

        foreach (Entity e in SceneSystem.SceneInstance.RootScene.Entities)
            if (e.Get<DamageLabel>() is { } l)
                Console.WriteLine(string.Format(HudRenderer.Numbers, "[label] {0}: \"{1}\" at {2:F1} {3:F1} {4:F1}, UI {5}, world y {6:F1}, width {7:F0}",
                    e.Name, l.Text, e.Transform.Position.X, e.Transform.Position.Y, e.Transform.Position.Z,
                    e.Get<Stride.Engine.UIComponent>() is { } ui ? (ui.Enabled ? "on" : "off") : "missing",
                    e.Transform.WorldMatrix.TranslationVector.Y,
                    e.Get<Stride.Engine.UIComponent>()?.Page?.RootElement?.ActualWidth ?? -1f));
    }

    /// <summary>KOTH_POSLOG: one line per new tick of the own body, a marker on F8.</summary>
    void LogPosition()
    {
        if (posLogPath == null || Find<PlayerCamera>()?.Motor is not { } motor
            || Find<HillBoard>()?.Simulation is not { } simulation)
            return;
        if (posLog == null)
        {
            posLog = new StreamWriter(posLogPath, append: true) { AutoFlush = true };
            posLog.WriteLine("# tick x y z vx vy vz grounded (Stride metres)");
        }
        Vector3 p = motor.Position, v = motor.Velocity;
        if (KeyPresses.Pressed(Input, Keys.F8))
        {
            posLog.WriteLine(FormattableString.Invariant($"# mark {simulation.CurrentTick} {p.X:F3} {p.Y:F3} {p.Z:F3}"));
            Console.WriteLine(FormattableString.Invariant($"[poslog] mark at {p.X:F2}, {p.Y:F2}, {p.Z:F2}"));
        }
        if (simulation.CurrentTick == posTick)
            return;
        posTick = simulation.CurrentTick;
        posLog.WriteLine(FormattableString.Invariant(
            $"{posTick} {p.X:F3} {p.Y:F3} {p.Z:F3} {v.X:F2} {v.Y:F2} {v.Z:F2} {(motor.IsGrounded ? 1 : 0)}"));
    }

    T Find<T>() where T : EntityComponent
    {
        Scene scene = SceneSystem.SceneInstance?.RootScene;
        if (scene == null)
            return null;
        foreach (Entity e in scene.Entities)
            if (e.Get<T>() is { } found)
                return found;
        return null;
    }
}

#if !WINDOWS
/// <summary>The .NET 11 apphost and NativeAOT binaries no longer pull in libstdc++, but
/// Stride's libstrideaudio.so expects its symbols in the global scope. Loading it
/// RTLD_GLOBAL before the engine starts restores that.</summary>
static class StdCxxPreload
{
    const int RtldNow = 0x002;
    const int RtldGlobal = 0x100;

    [DllImport("libc", EntryPoint = "dlopen")]
    static extern IntPtr DlOpen(string file, int mode);

    public static void Run()
    {
        if (DlOpen("libstdc++.so.6", RtldNow | RtldGlobal) == IntPtr.Zero)
            Console.Error.WriteLine("libstdc++.so.6 could not be loaded, audio is off.");
    }
}
#endif

static class SdlGrab
{
#if WINDOWS
    const string Lib = "SDL2";
#else
    const string Lib = "libSDL2-2.0.so";
#endif

    [DllImport(Lib)] public static extern void SDL_SetWindowGrab(nint window, int grabbed);
    [DllImport(Lib)] public static extern int SDL_SetRelativeMouseMode(int enabled);
    [DllImport(Lib)] public static extern nint SDL_GetKeyboardFocus();
    [DllImport(Lib)] public static extern void SDL_PumpEvents();
    [DllImport(Lib)] public static extern int SDL_SetWindowFullscreen(nint window, uint flags);

    /// <summary>SDL_WINDOW_FULLSCREEN_DESKTOP: borderless at the desktop's size; SDL_WINDOW_FULLSCREEN: the screen's mode switched.</summary>
    public const uint FullscreenDesktop = 0x1001, Fullscreen = 0x1;
    [DllImport(Lib)] public static extern void SDL_SetWindowSize(nint window, int w, int h);
    [DllImport(Lib)] public static extern void SDL_MaximizeWindow(nint window);
    [DllImport(Lib)] public static extern int SDL_SetWindowDisplayMode(nint window, ref DisplayMode mode);

    /// <summary>SDL_DisplayMode; format and refresh rate 0 let SDL pick the closest the screen has.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayMode { public uint Format; public int W, H, RefreshRate; public nint DriverData; }
    [DllImport(Lib)] public static extern void SDL_GetWindowSize(nint window, out int w, out int h);
    [DllImport(Lib)] public static extern int SDL_GetWindowDisplayIndex(nint window);
    [DllImport(Lib)] public static extern int SDL_GetDisplayBounds(int displayIndex, out Rect rect);
    [DllImport(Lib)] public static extern int SDL_GetNumVideoDisplays();
    [DllImport(Lib)] public static extern void SDL_RestoreWindow(nint window);
    [DllImport(Lib)] public static extern void SDL_SetWindowPosition(nint window, int x, int y);
    [DllImport(Lib)] public static extern void SDL_GetWindowPosition(nint window, out int x, out int y);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int X, Y, W, H; }
}
