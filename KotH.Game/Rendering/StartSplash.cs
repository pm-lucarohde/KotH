using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering.Compositing;

namespace KotH.Client.Rendering
{
    /// <summary>Stride's splash screen as SceneSystem 4.4.1 draws it (1 s the splash colour, 1 s fading in, held
    /// till 4 s and the scene is loaded, 1 s fading out), but with the scene loaded on the main thread while it is
    /// held: SceneSystem loads on workers, whose uploads race the first resize (vkDeviceWaitIdle without the queue
    /// lock; about one AOT start in six lost the device). The game gives SceneSystem no splash, so it loads nothing.</summary>
    public sealed class StartSplash
    {
        // SceneSystem's constants: the colour alone before the logo, the least time on screen, each fade.
        const double IntroSeconds = 1.0, LeastSeconds = 4.0;
        const float FadeSeconds = 1f;

        enum Stage { Intro, FadingIn, Showing, FadingOut, Done }

        readonly string logoUrl, sceneUrl, compositorUrl;
        readonly Color4 color;
        Texture logo;
        Stage stage;
        double fade;
        bool held, loaded;
        Scene scene;
        GraphicsCompositor compositor;

        StartSplash(string logoUrl, Color4 color, string sceneUrl, string compositorUrl)
        {
            this.logoUrl = logoUrl;
            this.color = color;
            this.sceneUrl = sceneUrl;
            this.compositorUrl = compositorUrl;
        }

        /// <summary>Before the scene system loads (PrepareContext): takes its first scene and compositor, which
        /// this loads instead. Null without a splash URL or a first scene, and nothing changes.</summary>
        public static StartSplash Take(SceneSystem system, string url)
        {
            if (url == null || system.InitialSceneUrl == null)
                return null;
            var splash = new StartSplash(url, system.SplashScreenColor, system.InitialSceneUrl, system.InitialGraphicsCompositorUrl);
            system.InitialSceneUrl = null;
            system.InitialGraphicsCompositorUrl = null;
            return splash;
        }

        public bool Done => stage == Stage.Done && scene == null;

        /// <summary>Before the frame: the logo first (straight on without one), the scene once the logo has
        /// been on screen a frame, and the scene to the scene system when the logo has faded out.</summary>
        public void Update(SceneSystem system, Action<string> mark)
        {
            var content = system.Services.GetSafeServiceAs<ContentManager>();
            if (logo == null && stage != Stage.Done)
            {
                if (content.Exists(logoUrl))
                    logo = content.Load<Texture>(logoUrl, ContentManagerLoaderSettings.StreamingDisabled);
                else
                    stage = Stage.Done;
            }
            if (!loaded && (stage == Stage.Showing && held || stage == Stage.Done))
            {
                loaded = true;
                scene = content.Load<Scene>(sceneUrl);
                if (compositorUrl != null && content.Exists(compositorUrl))
                    compositor = content.Load<GraphicsCompositor>(compositorUrl);
                mark("scene loaded");
            }
            if (stage != Stage.Done || scene == null)
                return;
            SceneInstance empty = system.SceneInstance;
            system.SceneInstance = new SceneInstance(system.Services, scene);
            if (compositor != null)
                system.GraphicsCompositor = compositor;
            empty?.Dispose();
            scene = null;
            mark("splash done");
        }

        /// <summary>After the scene system's draw, as SceneSystem.Draw ends with it.</summary>
        public void Draw(Game game, GameTime time)
        {
            CommandList list = game.GraphicsContext.CommandList;
            switch (stage)
            {
                case Stage.Intro:
                    list.Clear(list.RenderTarget, color);
                    if (time.Total.TotalSeconds > IntroSeconds)
                    {
                        stage = Stage.FadingIn;
                        fade = 0.0;
                    }
                    break;
                case Stage.FadingIn:
                {
                    float shown = MathUtil.SmoothStep((float)fade / FadeSeconds);
                    if (shown >= 1f)
                        stage = Stage.Showing;
                    fade += time.Elapsed.TotalSeconds;
                    Render(game, Color4.White * shown, BlendStates.AlphaBlend);
                    break;
                }
                case Stage.Showing:
                    Render(game, Color4.White, BlendStates.Default);
                    held = true;
                    if (time.Total.TotalSeconds > LeastSeconds && scene != null)
                    {
                        stage = Stage.FadingOut;
                        fade = 0.0;
                    }
                    break;
                case Stage.FadingOut:
                {
                    float shown = 1f - MathUtil.SmoothStep((float)fade / FadeSeconds);
                    if (shown <= 0f)
                        stage = Stage.Done;
                    fade += time.Elapsed.TotalSeconds;
                    Render(game, Color4.White * shown, BlendStates.AlphaBlend);
                    break;
                }
            }
        }

        // SceneSystem.RenderSplashScreen: the colour, then the logo in the middle square of the target.
        void Render(Game game, Color4 tint, BlendStateDescription blend)
        {
            CommandList list = game.GraphicsContext.CommandList;
            Texture target = list.RenderTarget;
            list.Clear(target, color);
            int width = target.Width, height = target.Height, side = Math.Min(width, height);
            Viewport before = list.Viewport;
            var square = new Viewport((width - side) / 2, (height - side) / 2, side, side);
            list.SetViewport(ref square);
            game.GraphicsContext.DrawTexture(logo, tint, blend);
            list.SetViewport(ref before);
        }
    }
}
