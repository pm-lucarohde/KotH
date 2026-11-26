using KotH.Client.Gamepad;
using KotH.Shared.Combat;

namespace KotH.Client.Player
{
    /// <summary>The gamepad shakes for the own figure's hits: hard when it is hit, briefly when the own
    /// cane, spore or dash struck someone (the victim's hit tick is the strike's). Started where the
    /// recoil starts, so a rollback never shakes twice.</summary>
    public sealed partial class PlayerVisualSystem
    {
        Blaster armsBlaster;
        Dash armsDash;

        void Rumble(Figure f, int tick)
        {
            if (armsMotor == null || !Pad.Active)
                return;
            if (f.Motor == armsMotor)
                Pad.Rumble(0.55f, 0.8f, 220);
            else if (armsMelee?.LastSwingTick == tick || armsBlaster?.LastImpactTick == tick || armsDash?.LastSlamTick == tick)
                Pad.Rumble(0.15f, 0.45f, 90);
        }
    }
}
