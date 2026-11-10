using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using KotH.MapFormat;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;

namespace KotH.MapKit
{
    /// <summary>The kit's game: runs the map scene, exports it once it shows (the camera's view
    /// becomes the preview) and lists the result. F5 exports again, Esc quits. Arguments: --export
    /// (export and quit, exit code 1 on errors), --out DIR (maps folder), --scene URL.</summary>
    public sealed class KitGame : Game
    {
        public static int Main(string[] args)
        {
            using var game = new KitGame(args);
            game.Run();
            return game.exitCode;
        }

        readonly bool once;
        readonly string scene;
        readonly string mapsRoot;
        int frames, exitCode, pending = 1;
        readonly List<(string Text, Color Color)> lines = new List<(string, Color)>();
        readonly List<ModelComponent> hidden = new List<ModelComponent>();

        void Hide(Scene scene)
        {
            foreach (Entity e in scene.Entities)
                if (e.Transform.Parent == null)
                    Hide(e, false);
            foreach (Scene child in scene.Children)
                Hide(child);
        }

        void Hide(Entity e, bool helper)
        {
            helper |= e.Get<MapIgnoreComponent>() != null || e.Get<MapZoneComponent>() != null || e.Get<MapSpawnComponent>() != null;
            if (helper)
                foreach (EntityComponent c in e.Components)
                    if (c is ModelComponent m && m.Enabled)
                    {
                        m.Enabled = false;
                        hidden.Add(m);
                    }
            foreach (TransformComponent child in e.Transform.Children)
                Hide(child.Entity, helper);
        }

        KitGame(string[] args)
        {
            mapsRoot = KitPaths.MapsFolder();
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--export": once = true; break;
                    case "--out" when i + 1 < args.Length: mapsRoot = Path.GetFullPath(args[++i]); break;
                    case "--scene" when i + 1 < args.Length: scene = args[++i]; break;
                }
        }

        protected override void Initialize()
        {
            base.Initialize();
            Window.Title = "KotH map kit";
            if (scene != null)
                SceneSystem.InitialSceneUrl = scene;
        }

        protected override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            if (Input.IsKeyPressed(Keys.Escape))
                Exit();
            if (Input.IsKeyPressed(Keys.F5) && pending == 0)
                pending = 1;
            // The frame before the export shows the note; the export frame draws no text and none
            // of the kit's helpers (zone box, bounds, figures), so the preview shows the map only.
            DebugTextSystem.Visible = pending != 2;
            if (pending == 2 && hidden.Count == 0 && SceneSystem.SceneInstance?.RootScene is Scene root)
                Hide(root);
            if (pending == 1 && frames > 20)
                Say(new[] { ("Exporting ...", Color.White) });
            for (int i = 0; i < lines.Count; i++)
                DebugTextSystem.Print(lines[i].Text, new Int2(12, 12 + 18 * i), lines[i].Color, TimeSpan.FromSeconds(0.1));
        }

        protected override void Draw(GameTime gameTime)
        {
            base.Draw(gameTime);
            frames++;
            if (pending == 0 || frames <= 20 || SceneSystem.SceneInstance?.RootScene == null)
                return;
            if (pending == 1)
            {
                pending = 2;
                return;
            }
            pending = 0;
            Export();
            foreach (ModelComponent m in hidden)
                m.Enabled = true;
            hidden.Clear();
            if (once)
                Exit();
        }

        void Export()
        {
            var watch = Stopwatch.StartNew();
            var said = new List<(string, Color)>();
            try
            {
                byte[] preview = Preview();
                var exporter = new MapExporter(Services);
                MapBuilder.Result r = exporter.Export(SceneSystem.SceneInstance.RootScene, mapsRoot, preview, out List<string> notes);
                exitCode = r.Ok ? 0 : 1;
                if (r.Ok)
                {
                    said.Add(($"Exported {r.Package.Id} in {watch.Elapsed.TotalSeconds:F1} s", Color.LightGreen));
                    said.Add(($"  {r.Package.Folder}", Color.White));
                    said.Add(($"  Test: KotH --offline --map-file \"{r.Package.Folder}\"", Color.White));
                    said.Add(($"  SteamCMD: workshop_build_item \"{r.Vdf}\"", Color.White));
                }
                else
                    said.Add(("Not exported:", Color.OrangeRed));
                foreach (string e in r.Errors)
                    said.Add(($"  Error: {e}", Color.OrangeRed));
                foreach (string w in r.Warnings)
                    said.Add(($"  Warning: {w}", Color.Yellow));
                foreach (string n in notes)
                    said.Add(($"  Note: {n}", Color.Yellow));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException
                                      || e is FormatException || e is InvalidOperationException)
            {
                exitCode = 1;
                said.Add(($"Export failed: {e.Message}", Color.OrangeRed));
            }
            said.Add(("F5: export again   Esc: quit", Color.Gray));
            Say(said);
        }

        void Say(IEnumerable<(string, Color)> text)
        {
            lines.Clear();
            foreach ((string t, Color c) in text)
            {
                lines.Add((t, c));
                if (t != "Exporting ...")
                    Console.WriteLine(t);
            }
        }

        // The view's centre square, box-filtered to 512², opaque.
        byte[] Preview()
        {
            const int Size = 512;
            using Image image = GraphicsDevice.Presenter.BackBuffer.GetDataAsImage(GraphicsContext.CommandList);
            PixelBuffer pixels = image.PixelBuffer[0];
            byte[] data = pixels.GetPixels<byte>();
            bool bgra = pixels.Format == PixelFormat.B8G8R8A8_UNorm || pixels.Format == PixelFormat.B8G8R8A8_UNorm_SRgb
                        || pixels.Format == PixelFormat.B8G8R8X8_UNorm || pixels.Format == PixelFormat.B8G8R8X8_UNorm_SRgb;
            int w = pixels.Width, h = pixels.Height, side = Math.Min(w, h), x0 = (w - side) / 2, y0 = (h - side) / 2;
            var rgba = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int sx0 = x0 + x * side / Size, sx1 = Math.Max(sx0 + 1, x0 + (x + 1) * side / Size);
                    int sy0 = y0 + y * side / Size, sy1 = Math.Max(sy0 + 1, y0 + (y + 1) * side / Size);
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int at = (sy * w + sx) * 4;
                            r += data[at + (bgra ? 2 : 0)];
                            g += data[at + 1];
                            b += data[at + (bgra ? 0 : 2)];
                            n++;
                        }
                    int o = (y * Size + x) * 4;
                    rgba[o] = (byte)(r / n);
                    rgba[o + 1] = (byte)(g / n);
                    rgba[o + 2] = (byte)(b / n);
                    rgba[o + 3] = 255;
                }
            return TextureReader.Png(rgba, Size, Size);
        }
    }
}
