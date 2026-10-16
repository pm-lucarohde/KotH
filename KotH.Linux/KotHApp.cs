using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using KotH.Client.Hud;
using KotH.Client.Player;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;

StdCxxPreload.Run();

using var game = new KotHGame();
game.Run();

/// <summary>
/// The game, plus one test aid: with KOTH_SHOT set to a path it shows the
/// F3 readout, holds the view at KOTH_SHOT_YAW / _PITCH / _FOV if given,
/// saves the back buffer as PNG after KOTH_SHOT_SECONDS (default 7, past
/// Stride's splash screen), reports the label font and quits. Taken from
/// inside the game on purpose, a desktop screenshot would capture whatever
/// else is on the screen.
/// </summary>
sealed class KotHGame : Game
{
    readonly string shotPath = Environment.GetEnvironmentVariable("KOTH_SHOT");
    readonly double shotSeconds = Number("KOTH_SHOT_SECONDS") ?? 7.0;
    readonly double? shotYaw = Number("KOTH_SHOT_YAW");
    readonly double shotPitch = Number("KOTH_SHOT_PITCH") ?? 0.0;
    readonly double? shotFov = Number("KOTH_SHOT_FOV");

    public KotHGame()
    {
        // Warnings and errors reach the terminal in Release too; otherwise
        // a script failing in Start leaves nothing but a black window.
        ConsoleLogMode = ConsoleLogMode.Always;
        ConsoleLogLevel = LogMessageType.Warning;

        // Uncapped like the Unity build: no wait for the display's refresh.
        GraphicsDeviceManager.SynchronizeWithVerticalRetrace = true;
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (shotPath == null)
            return;

        if (Find<PerformanceOverlay>() is { } overlay)
            overlay.Visible = true;
        if (shotYaw.HasValue && Find<PlayerInputReader>() is { } reader)
            reader.Look((float)shotYaw.Value, (float)shotPitch);
        if (shotFov.HasValue && Find<CameraComponent>() is { } camera)
            camera.VerticalFieldOfView = (float)shotFov.Value;
    }

    static double? Number(string variable) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value) ? value : null;

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);

        if (shotPath == null || gameTime.Total.TotalSeconds < shotSeconds)
            return;

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

        Console.WriteLine($"[shot] {shotPath} nach {gameTime.Total.TotalSeconds:F1} s, {gameTime.FramePerSecond:F0} fps, " +
                          $"Gier {Find<PlayerInputReader>()?.Yaw:F2}°");
        ReportFont();
        Exit();
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
