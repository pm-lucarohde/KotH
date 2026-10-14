using UnityEngine;

namespace KotH.Client
{
    /// <summary>
    /// How far the current frame sits between the last two simulation ticks.
    /// Presentation only: it smooths the 40 Hz tick for the eye and never
    /// feeds back into the simulation.
    /// </summary>
    public static class TickInterpolation
    {
        // Time.fixedTime is the timestamp of the last FixedUpdate.
        public static float Alpha =>
            Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
    }
}
