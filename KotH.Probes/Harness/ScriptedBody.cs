using System;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;

namespace KotH.Probes.Harness
{
    /// <summary>
    /// A simulation body a probe steers. Needed whenever a measurement has to run
    /// THROUGH SimulationSystem instead of around it: the body push exists only
    /// there, so a probe that ticks the motor directly would measure nothing.
    /// </summary>
    [DataContract]
    public sealed class ScriptedBody : SimulationBody
    {
        public MoveInput Input;

        // Raised while the system gathers inputs, before anything of the tick
        // has run. The probe sets the input it wants for this very tick here.
        public Action BeforeTick;

        public override MoveInput ReadInput()
        {
            BeforeTick?.Invoke();
            return Input;
        }
    }
}
