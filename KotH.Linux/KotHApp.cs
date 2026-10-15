using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;

StdCxxPreload.Run();

using var game = new KotHGame();
game.Run();

/// <summary>
/// The game, plus one test aid: with KOTH_SHOT set to a path it saves the
/// back buffer as PNG after KOTH_SHOT_SECONDS (default 5) and quits. Taken
/// from inside the game on purpose, a desktop screenshot would capture
/// whatever else is on the screen.
/// </summary>
sealed class KotHGame : Game
{
    readonly string shotPath = Environment.GetEnvironmentVariable("KOTH_SHOT");
    readonly double shotSeconds = double.TryParse(Environment.GetEnvironmentVariable("KOTH_SHOT_SECONDS"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 5.0;

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

        Console.WriteLine($"[shot] {shotPath} nach {gameTime.Total.TotalSeconds:F1} s, {gameTime.FramePerSecond:F0} fps");
        Exit();
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
