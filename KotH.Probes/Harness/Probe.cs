using System;
using KotH.Shared.Hosting;
using KotH.Shared.Simulation;

namespace KotH.Probes.Harness
{
    /// <summary>
    /// One probe: Start once, then FixedUpdate every tick until it calls
    /// Exit, the way Unity's play mode drove the MonoBehaviour probes. The
    /// SimulationSystem ticks after the probe in every step, so a dummy in
    /// the scene keeps living exactly as it did in Unity.
    /// </summary>
    public abstract class Probe
    {
        /// <summary>Unity's Time.fixedDeltaTime.</summary>
        protected const float FixedDeltaTime = SimulationSystem.TickSeconds;

        protected HeadlessWorld World { get; private set; }

        int? exitCode;

        /// <summary>Seconds of simulated time, Unity's Time.time in play mode.</summary>
        protected float Time { get; private set; }

        protected abstract string Tag { get; }

        protected virtual void Start() { }
        protected virtual void FixedUpdate() { }

        protected void Log(string message) => Console.WriteLine(message);

        protected void LogError(string message) => Console.WriteLine(message);

        /// <summary>Ends the run with this exit code, EditorApplication.Exit.</summary>
        protected void Exit(int code)
        {
            if (exitCode == null)
                exitCode = code;
        }

        /// <summary>Runs the probe to its end and returns its exit code.</summary>
        public int Run(HeadlessWorld world, int maxTicks)
        {
            World = world;
            Log("[" + Tag + "] entering play mode");

            Start();
            for (int tick = 0; exitCode == null && tick < maxTicks; tick++)
            {
                FixedUpdate();
                if (exitCode != null)
                    break;

                world.Simulation.Tick();
                Time += FixedDeltaTime;
            }

            if (exitCode == null)
            {
                LogError("[" + Tag + "] Watchdog: keine Rückmeldung, Abbruch");
                return 2;
            }

            return exitCode.Value;
        }
    }
}
