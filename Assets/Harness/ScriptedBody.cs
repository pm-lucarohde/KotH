#if UNITY_EDITOR
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using UnityEngine;

/// <summary>
/// A simulation body a probe steers. Needed whenever a measurement has to run
/// THROUGH SimulationSystem instead of around it: the body push exists only
/// there, so a probe that ticks the motor directly would measure nothing.
/// </summary>
public sealed class ScriptedBody : SimulationBody
{
    public MoveInput Input;

    // Raised inside the system's FixedUpdate, after contacts are resolved and
    // right before the tick. The only point where a probe sees a push that
    // has landed but has not been acted on yet.
    public System.Action BeforeTick;

    public override MoveInput ReadInput()
    {
        BeforeTick?.Invoke();
        return Input;
    }
}
#endif
