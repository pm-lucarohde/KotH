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

// Six render threads instead of all 24: Stride's workers spin after every
// parallel loop, and on this scene fewer of them are faster and far lighter
// on the CPU (measured 20.10.2026, Luca's choice). The variable still wins.
if (Environment.GetEnvironmentVariable("STRIDE_MAX_PARALLELISM") == null)
    Dispatcher.MaxDegreeOfParallelism = 6;

using var game = new KotHGame();
game.Run();

/// <summary>
/// The game: starts in borderless fullscreen, F11 switches to the window and
/// back. Plus test aids. With KOTH_SHOT set to a path it shows the
/// F3 readout, holds the view at KOTH_SHOT_YAW / _PITCH / _FOV if given,
/// KOTH_SHOT_THIRD=1 steps back into third person, KOTH_SHOT_AT=x,y,z puts
/// the player's body there, again whenever the round places it (Stride metres), KOTH_SHOT_FACE=deg
/// turns it that way when it does (the figure stays so in third person), KOTH_SHOT_SWING=1 swings
/// once a second before the shot and reports the hit, KOTH_SHOT_SHAKE=1 takes a hit
/// just before it and reports how far the camera shake tilts the view,
/// KOTH_SHOT_HIT=deg[,s] hits every figure s (default 0.1) seconds before it, from deg round its body (0 front, 90 right),
/// KOTH_SHOT_BLINK=1 has every figure blink so the shot shows shut lids,
/// KOTH_SHOT_STRIKE=s[,s2] has every figure swing its cane at these times (only shown),
/// KOTH_SHOT_MOVE=x,y[,sprint] holds the move keys (D and W are +1),
/// KOTH_SHOT_CROUCH=s presses crouch at s seconds and holds it (out of a sprint: a slide),
/// KOTH_SHOT_JUMP=s[,s2] presses jump at s seconds (and again at s2: the double jump),
/// KOTH_SHOT_TURN=deg,s[,orbit] turns the held view by deg at s seconds in first person
/// (with KOTH_SHOT_THIRD the step back comes 0.2 s later, the camera circling the
/// figure by orbit degrees, to see head and body), KOTH_SHOT_VIDEO=s also writes the last s seconds
/// before the shot as raw frames at 30 fps (path.rgba, size and format in the log, for ffmpeg),
/// saves the back buffer as PNG after KOTH_SHOT_SECONDS (default 3), reports
/// the label font and quits. Taken from
/// inside the game on purpose, a desktop screenshot would capture whatever
/// else is on the screen. For performance runs: KOTH_SIZE=1920x1080 sets the
/// window, KOTH_VSYNC=0 or 1 overrides V-Sync, KOTH_FPS=N caps the frame rate
/// like --fps, and KOTH_FRAMELOG=path writes
/// every frame time in milliseconds, one per line, when the game ends. For the music:
/// KOTH_MUSIC_VOLUME=0..1 overrides the volume, KOTH_MUSIC_TAIL=s starts every single-play
/// track s seconds before its end, so the changes come quickly. KOTH_SFX_VOLUME=0..1 scales the
/// ambient and the figures' sounds (silent with KOTH_MUSIC_VOLUME=0 unless set), KOTH_SOUNDLOG=1
/// logs each of those. KOTH_HEADLESS=1 never shows
/// the window and renders into a texture of KOTH_SIZE (default 2560x1440), for shots.
/// KOTH_EFFECTLOG=path records the effect permutations used (EffectRecorder).
/// </summary>
sealed class KotHGame : Game
{
    readonly string shotPath = Environment.GetEnvironmentVariable("KOTH_SHOT");
    readonly double shotSeconds = Number("KOTH_SHOT_SECONDS") ?? 3.0;
    readonly double? shotYaw = Number("KOTH_SHOT_YAW");
    readonly double shotPitch = Number("KOTH_SHOT_PITCH") ?? 0.0;
    readonly double? shotFov = Number("KOTH_SHOT_FOV");
    readonly bool shotThird = Environment.GetEnvironmentVariable("KOTH_SHOT_THIRD") == "1";
    Vector3? shotAt = Point("KOTH_SHOT_AT");
    readonly double? shotFace = Number("KOTH_SHOT_FACE");
    int shotPlaced = -1;
    bool shotSwing = Environment.GetEnvironmentVariable("KOTH_SHOT_SWING") == "1";
    bool shotShake = Environment.GetEnvironmentVariable("KOTH_SHOT_SHAKE") == "1";
    bool shotBlink = Environment.GetEnvironmentVariable("KOTH_SHOT_BLINK") == "1";
    double? shotHit = Number("KOTH_SHOT_HIT", 0);
    readonly double shotHitLead = Number("KOTH_SHOT_HIT", 1) ?? 0.1;
    string shotMove = Environment.GetEnvironmentVariable("KOTH_SHOT_MOVE");
    double? shotCrouch = Number("KOTH_SHOT_CROUCH");
    readonly double shotVideo = Number("KOTH_SHOT_VIDEO") ?? 0.0;
    FileStream video;
    double videoAt = double.NaN;
    readonly double[] shotTurn = Environment.GetEnvironmentVariable("KOTH_SHOT_TURN")?.Split(',')
        .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    readonly Queue<double> shotJumps = new Queue<double>(
        (Environment.GetEnvironmentVariable("KOTH_SHOT_JUMP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)));
    readonly Queue<double> shotStrikes = new Queue<double>(
        (Environment.GetEnvironmentVariable("KOTH_SHOT_STRIKE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)));
    readonly string frameLogPath = Environment.GetEnvironmentVariable("KOTH_FRAMELOG");
    readonly double? musicVolume = Number("KOTH_MUSIC_VOLUME");
    readonly double? musicTail = Number("KOTH_MUSIC_TAIL");
    readonly double? sfxVolume = Number("KOTH_SFX_VOLUME");
    readonly bool soundLog = Environment.GetEnvironmentVariable("KOTH_SOUNDLOG") == "1";
    bool sfxSet;
    readonly bool headless = Environment.GetEnvironmentVariable("KOTH_HEADLESS") == "1";
    bool musicSet;
    Int2 windowedSize;
    bool confined;

    // Fullscreen before loading, see Initialize; the size asked for then is
    // where F11 returns to. A fixed KOTH_SIZE stays a window.
    readonly bool startFullscreen = Environment.GetEnvironmentVariable("KOTH_SIZE") == null &&
                                    Environment.GetEnvironmentVariable("KOTH_HEADLESS") != "1";
    int restoreFrames;

    // Ten minutes at 2000 FPS, allocated once so logging never allocates.
    float[] frameLog;
    int frameCount;

    public KotHGame()
    {
        // Warnings and errors reach the terminal in Release too; otherwise
        // a script failing in Start leaves nothing but a black window.
        ConsoleLogMode = ConsoleLogMode.Always;
        ConsoleLogLevel = LogMessageType.Warning;

        // Renders as fast as it can, without waiting for the display's
        // refresh (Luca's choice, earlier it waited like Unity's "Ultra").
        GraphicsDeviceManager.SynchronizeWithVerticalRetrace =
            Number("KOTH_VSYNC") is { } vsync && vsync != 0.0;

        if (frameLogPath != null)
            frameLog = new float[600 * 2000];

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

        // At most --fps N (or KOTH_FPS) frames per second, unlimited without; the
        // last millisecond is spun, so the frames come evenly. Hidden, it counts as minimised.
        if ((LaunchOptions.Current.Fps ?? (int?)Number("KOTH_FPS")) is { } fps && fps > 0)
            foreach (var rate in headless ? [WindowMinimumUpdateRate, MinimizedMinimumUpdateRate] : new[] { WindowMinimumUpdateRate })
            {
                rate.SetMaxFrequency(fps);
                rate.SetToPreciseAuto();
            }
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

    /// <summary>
    /// Waits for the window manager to show and focus the window, then goes
    /// fullscreen at once and captures the mouse, each wait two seconds at
    /// most. Both used to wait for the first frame after loading: until then
    /// the pointer could leave for the other screen, and a grab only holds it
    /// in a window that covers the whole screen.
    /// </summary>
    protected override void Initialize()
    {
        base.Initialize();
        KotH.Client.Rendering.EffectRecorder.Attach(EffectSystem);
        if (headless)
            return;
        if (Window.NativeWindow?.NativeWindow is not Stride.Graphics.SDL.Window sdl)
            return;

        // Pumping only moves the window's events into SDL's queue; the game
        // still reads every one of them later.
        PumpUntil(() => SdlGrab.SDL_GetKeyboardFocus() == sdl.SdlHandle);

        if (LaunchOptions.Current.Display is { } display)
            MoveToDisplay(sdl.SdlHandle, display);

        if (startFullscreen)
        {
            // The size the window manager gave the new window is where F11
            // returns to. Stride applies the switch only in its loop, after
            // loading; the window goes now, the device follows.
            ToggleFullscreen();
            SdlGrab.SDL_GetWindowSize(sdl.SdlHandle, out windowedSize.X, out windowedSize.Y);
            SdlGrab.SDL_SetWindowFullscreen(sdl.SdlHandle, SdlGrab.FullscreenDesktop);
            PumpUntil(() => CoversDisplay(sdl.SdlHandle));
            if (LaunchOptions.Current.Display != null)
            {
                SdlGrab.SDL_GetWindowPosition(sdl.SdlHandle, out int fx, out int fy);
                Console.WriteLine($"[monitor] Vollbild bei {fx},{fy} (Monitor {LaunchOptions.Current.Display} von links gewünscht)");
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

    protected override void EndRun()
    {
        base.EndRun();

        if (frameLog == null)
            return;
        using var writer = new StreamWriter(frameLogPath);
        for (int i = 0; i < frameCount; i++)
            writer.WriteLine(frameLog[i].ToString("R", CultureInfo.InvariantCulture));
        Console.WriteLine($"[frames] {frameCount} Frames nach {frameLogPath}");
    }

    protected override void Update(GameTime gameTime)
    {
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

        if (shotPath == null)
            return;

        if (Find<PerformanceOverlay>() is { } overlay)
            overlay.Visible = true;
        double now = TestSeconds(gameTime);
        bool turned = shotTurn != null && now >= shotTurn[1];
        bool orbit = turned && shotThird && shotTurn.Length > 2 && now >= shotTurn[1] + 0.2;
        if ((shotYaw.HasValue || shotTurn != null) && Find<PlayerInputReader>() is { } reader)
            reader.Look((float)((shotYaw ?? 0.0) + (turned ? shotTurn[0] : 0.0) + (orbit ? shotTurn[2] : 0.0)), (float)shotPitch);
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
            // Also after the round has put everyone at its start (Neu: at random).
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
            while (shotJumps.Count > 0 && TestSeconds(gameTime) >= shotJumps.Peek())
            {
                player.Jump();
                shotJumps.Dequeue();
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

    /// <summary>
    /// SDL's relative mode only hides the pointer here (X11 on Wayland): it
    /// never grabs, so the pointer left the fullscreen window for the other
    /// screen. While the game has the mouse, the window grabs it as well.
    /// </summary>
    void ConfinePointer()
    {
        bool confine = Input.IsMousePositionLocked && Window.Focused;
        if (confine == confined || Window.NativeWindow?.NativeWindow is not Stride.Graphics.SDL.Window sdl)
            return;
        SdlGrab.SDL_SetWindowGrab(sdl.SdlHandle, confine ? 1 : 0);
        confined = confine;
    }

    /// <summary>
    /// Borderless instead of exclusive: no mode switch, so toggling is quick
    /// and other windows can still be reached. The back buffer follows the
    /// window, so the desktop size never has to be asked for. Set on the
    /// device manager: Stride 4.4 drops Window.IsFullscreen, its handler runs
    /// the orientation update instead of the fullscreen one.
    /// </summary>
    void ToggleFullscreen()
    {
        GraphicsDeviceManager manager = GraphicsDeviceManager;
        if (!manager.IsFullScreen)
        {
            Rectangle bounds = Window.ClientBounds;
            windowedSize = new Int2(bounds.Width, bounds.Height);
            Window.FullscreenIsBorderlessWindow = true;
            restoreFrames = 0;
        }
        else
        {
            manager.PreferredBackBufferWidth = windowedSize.X;
            manager.PreferredBackBufferHeight = windowedSize.Y;
            restoreFrames = 30;
        }

        manager.IsFullScreen = !manager.IsFullScreen;
        manager.ApplyChanges();
    }

    /// <summary>
    /// Stride asks for the old window size while the window is still
    /// fullscreen, and the window manager then brings back the fullscreen
    /// size. Asked again once the window is back, for half a second at most.
    /// </summary>
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
                Console.WriteLine($"[frames] erster Frame {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms nach Prozessstart");
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

        Console.WriteLine($"[shot] {shotPath} nach {TestSeconds(gameTime):F1} s, {gameTime.FramePerSecond:F0} fps, " +
                          $"Gier {Find<PlayerInputReader>()?.Yaw:F2}°");
        if (Find<MusicPlayer>() is { } music)
            Console.WriteLine($"[musik] {music.Readout}");
        if (Find<PlayerInputReader>()?.Entity.Get<KotH.Shared.Combat.MeleeAttack>() is { } melee)
            Console.WriteLine($"[swing] {melee.SwingCount} Schläge, {melee.HitCount} Treffer, " +
                              $"zuletzt {melee.LastHitDistance:F3} m");
        if (Find<PlayerInputReader>() is { } look && Find<PlayerCamera>() is { } view)
        {
            Quaternion still = KotH.Shared.Mathematics.UMath.Euler(look.Pitch, look.Yaw);
            float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(still, view.Entity.Transform.Rotation)));
            Console.WriteLine(string.Format(HudRenderer.Numbers, "[wackeln] Bild um {0:F2}° gegen den Blick gekippt",
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

    /// <summary>
    /// MeleeProbe's font check, moved here because measuring needs the
    /// loaded font and so a graphics device: a font without glyphs draws
    /// nothing while every other check stays green.
    /// </summary>
    void ReportFont()
    {
        DamageLabel label = Find<DamageLabel>();
        if (label == null)
        {
            Console.WriteLine("[schrift] kein DamageLabel in der Szene");
            return;
        }

        Vector2 size = label.Measure("1000,0 %");
        Console.WriteLine(string.Format(HudRenderer.Numbers,
            "[schrift] {0}, \"1000,0 %\" misst {1:F3} x {2:F3} m (muss über null sein), " +
            "Schriftgröße {3:F2} m, aktueller Text \"{4}\", Label auf y {5:F3}",
            label.Font?.FontType, size.X, size.Y, label.FontSize, label.Text, label.Entity.Transform.Position.Y));

        foreach (Entity e in SceneSystem.SceneInstance.RootScene.Entities)
            if (e.Get<DamageLabel>() is { } l)
                Console.WriteLine(string.Format(HudRenderer.Numbers, "[schild] {0}: \"{1}\" bei {2:F1} {3:F1} {4:F1}, UI {5}, Welt-y {6:F1}, Breite {7:F0}",
                    e.Name, l.Text, e.Transform.Position.X, e.Transform.Position.Y, e.Transform.Position.Z,
                    e.Get<Stride.Engine.UIComponent>() is { } ui ? (ui.Enabled ? "an" : "aus") : "fehlt",
                    e.Transform.WorldMatrix.TranslationVector.Y,
                    e.Get<Stride.Engine.UIComponent>()?.Page?.RootElement?.ActualWidth ?? -1f));
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
/// <summary>
/// The .NET 11 apphost and NativeAOT binaries no longer pull in libstdc++,
/// but Stride's libstrideaudio.so expects its symbols in the global scope.
/// Loading it RTLD_GLOBAL before the engine starts restores that.
/// </summary>
static class StdCxxPreload
{
    const int RtldNow = 0x002;
    const int RtldGlobal = 0x100;

    [DllImport("libc", EntryPoint = "dlopen")]
    static extern IntPtr DlOpen(string file, int mode);

    public static void Run()
    {
        if (DlOpen("libstdc++.so.6", RtldNow | RtldGlobal) == IntPtr.Zero)
            Console.Error.WriteLine("libstdc++.so.6 konnte nicht geladen werden, Audio fällt aus.");
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

    /// <summary>SDL_WINDOW_FULLSCREEN_DESKTOP: borderless at the desktop's size.</summary>
    public const uint FullscreenDesktop = 0x1001;
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
