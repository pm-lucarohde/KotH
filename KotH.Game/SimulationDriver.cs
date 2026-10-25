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
    /// <summary>
    /// Runs the simulation at its fixed 40 Hz from the variable frame rate,
    /// the way Unity's FixedUpdate did: an accumulator, at most a third of a
    /// second caught up per frame. Input is collected first, so a press
    /// reaches the very next tick. Runs before every other script. In a
    /// networked game the session steps the world instead, on the shared clock.
    /// </summary>
    [DataContract("KotH.SimulationDriver")]
    public sealed class SimulationDriver : SyncScript
    {
        readonly TickClock clock = new TickClock();

        /// <summary>The local player's input, set by GameRoot.</summary>
        [DataMemberIgnore] public PlayerInputReader LocalInput { get; set; }

        /// <summary>Whether the mouse drives the view.</summary>
        [DataMemberIgnore] public bool Captured { get; private set; }

        /// <summary>
        /// The main menu is open: the mouse is free and belongs to it, Escape
        /// never quits and nothing ticks. Closing it grabs the mouse.
        /// </summary>
        [DataMemberIgnore]
        public bool InMenu
        {
            get => inMenu;
            set
            {
                inMenu = value;
                SetCaptured(!value);
            }
        }

        bool inMenu;

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

            bool grabbed = !inMenu && UpdateCursor();
            LocalInput?.Collect(Input, Captured && !grabbed);

            // Alone, the round starts once the load screen is gone; networked
            // the session starts then (GameRoot), and until then nothing ticks.
            LoadScreen cover = Services.GetService<LoadScreen>();
            cover?.Update(Game.UpdateTime.Elapsed.TotalSeconds);

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

        /// <summary>
        /// First Escape frees the mouse, a second one quits; two presses, so
        /// it never fires by accident. A click grabs the mouse back; true then,
        /// so that frame neither looks nor swings.
        /// </summary>
        bool UpdateCursor()
        {
            if (KeyPresses.Pressed(Input, Stride.Input.Keys.Escape))
            {
                if (Captured)
                    SetCaptured(false);
                else
                    ((GameBase)Game).Exit();
            }
            else if (!Captured && Input.IsMouseButtonPressed(Stride.Input.MouseButton.Left))
            {
                SetCaptured(true);
                return true;
            }

            return false;
        }

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

    /// <summary>
    /// How far the current frame sits between the last two simulation ticks.
    /// Presentation only: it smooths the 40 Hz tick for the eye and never
    /// feeds back into the simulation.
    /// </summary>
    public static class TickInterpolation
    {
        public static float Alpha { get; internal set; }
    }
}
