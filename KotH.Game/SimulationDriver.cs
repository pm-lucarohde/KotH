using KotH.Client.Hud;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Shared.Bots;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Engine;
using Stride.Games;

namespace KotH.Client
{
    /// <summary>Runs the simulation at 40 Hz from the frame rate like Unity's FixedUpdate (an
    /// accumulator, at most a third of a second per frame); input first, so a press reaches the
    /// next tick. Runs before every other script; networked, the session steps the world.</summary>
    [DataContract("KotH.SimulationDriver")]
    public sealed class SimulationDriver : SyncScript
    {
        readonly TickClock clock = new TickClock();

        /// <summary>The local player's input, set by GameRoot.</summary>
        [DataMemberIgnore] public PlayerInputReader LocalInput { get; set; }

        /// <summary>Whether the mouse drives the view.</summary>
        [DataMemberIgnore] public bool Captured { get; private set; }

        /// <summary>
        /// The main menu is open: the mouse is free and belongs to it and
        /// nothing ticks. Closing it grabs the mouse.
        /// </summary>
        [DataMemberIgnore]
        public bool InMenu
        {
            get => inMenu;
            set
            {
                inMenu = value;
                SetCaptured(!value && !paused);
            }
        }

        /// <summary>The pause menu is open: the mouse belongs to it, the own figure holds
        /// no key, the game runs on. Closing it grabs the mouse; that frame neither looks
        /// nor swings.</summary>
        [DataMemberIgnore]
        public bool Paused
        {
            get => paused;
            set
            {
                paused = value;
                SetCaptured(!value && !inMenu);
                settling = !value;
            }
        }

        bool inMenu, paused, settling;

        public SimulationDriver()
        {
            Priority = -1000;
        }

        public override void Start()
        {
            SetCaptured(!LaunchOptions.Current.ShowsMenu);
        }

        public override void Update()
        {
            SimulationSystem system = Services.GetService<SimulationSystem>();
            if (system == null)
                return;

            if (paused || inMenu)
                LocalInput?.Rest();
            else
                LocalInput?.Collect(Input, Captured && !settling);
            settling = false;

            // Alone, the round starts once the load screen is gone; networked
            // the session starts then (GameRoot), and until then nothing ticks.
            LoadScreen cover = Services.GetService<LoadScreen>();
            cover?.Update(Game.UpdateTime.Elapsed.TotalSeconds, Game.UpdateTime.FrameCount, Game.GraphicsContext.CommandList);

            if (Services.GetService<NetClient>() is { } net)
            {
                net.Update();
                TickInterpolation.Alpha = net.Alpha;
            }
            else if (!LaunchOptions.Current.Networked && !inMenu && cover is not { Covers: true })
            {
                int due = clock.Advance(Game.UpdateTime.Elapsed.TotalSeconds);
                for (int i = 0; i < due; i++)
                    system.Tick();
                TickInterpolation.Alpha = clock.Alpha;
            }

            // After the ticks: the states they made final go to the bots.
            Services.GetService<BotDirector>()?.Pump(BotBudget);
        }

        // Milliseconds a frame may spend on the bots without their own thread.
        const double BotBudget = 0.25;

        void SetCaptured(bool captured)
        {
            Captured = captured;
            if (captured)
                Input.LockMousePosition(true);
            else
                Input.UnlockMousePosition();
            Game.IsMouseVisible = !captured;
        }
    }

    /// <summary>How far the current frame sits between the last two simulation ticks.
    /// Presentation only: it smooths the 40 Hz tick for the eye and never feeds back into
    /// the simulation.</summary>
    public static class TickInterpolation
    {
        public static float Alpha { get; internal set; }
    }
}
