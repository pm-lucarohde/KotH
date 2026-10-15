using KotH.Client.Player;
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
    /// reaches the very next tick. Runs before every other script.
    /// </summary>
    [DataContract("KotH.SimulationDriver")]
    public sealed class SimulationDriver : SyncScript
    {
        readonly TickClock clock = new TickClock();

        /// <summary>The local player's input, set by GameRoot.</summary>
        [DataMemberIgnore] public PlayerInputReader LocalInput { get; set; }

        /// <summary>Whether the mouse drives the view.</summary>
        [DataMemberIgnore] public bool Captured { get; private set; }

        public SimulationDriver()
        {
            Priority = -1000;
        }

        public override void Start()
        {
            SetCaptured(true);
        }

        public override void Update()
        {
            SimulationSystem system = SimulationSystem.Active;
            if (system == null)
                return;

            bool grabbed = UpdateCursor();
            LocalInput?.Collect(Input, Captured && !grabbed);

            int due = clock.Advance(Game.UpdateTime.Elapsed.TotalSeconds);
            for (int i = 0; i < due; i++)
                system.Tick();

            TickInterpolation.Alpha = clock.Alpha;
        }

        /// <summary>
        /// First Escape frees the mouse, a second one quits; two presses, so
        /// it never fires by accident. A click grabs the mouse back; true then,
        /// so that frame neither looks nor swings.
        /// </summary>
        bool UpdateCursor()
        {
            if (Input.IsKeyPressed(Stride.Input.Keys.Escape))
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
