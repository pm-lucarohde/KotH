using KotH.Probes.Harness;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Measures how often the ground is lost while running DOWN a ramp. A jump
    /// needs IsGrounded, so every tick without it is a refused jump. Throwaway
    /// probe for that one question.
    /// </summary>
    public sealed class SlopeJumpProbe : Probe
    {
        protected override string Tag => "slope";

        const int TicksPerCase = 120;

        static readonly string[] Ramps = { "Ramp20", "Ramp35", "Ramp45" };

        PlayerMotor motor;

        int caseIndex = -1;
        int tick;
        float yaw;
        bool sprint;

        int notGrounded;
        int streak;
        int worstStreak;

        protected override void Start()
        {
            motor = Prototype.Open(World).Motor;
            Next();
        }

        void Next()
        {
            caseIndex++;
            if (caseIndex >= Ramps.Length * 2)
            {
                Exit(0);
                return;
            }

            UnityTransform t = Arena.Find(World, Ramps[caseIndex / 2]);
            sprint = caseIndex % 2 == 1;

            // Local +z points downhill, the pivot sits inside the slab.
            Vector3 surface = UMath.Add(t.position, UMath.Mul(t.up, t.localScale.Y * 0.5f));
            Vector3 downhill = UMath.Normalized(new Vector3(t.forward.X, 0f, t.forward.Z));
            yaw = UMath.Atan2(downhill.X, downhill.Z) * UMath.Rad2Deg;

            Vector3 start = UMath.Add(UMath.Sub(surface, UMath.Mul(downhill, t.localScale.Z * 0.35f)),
                                      UMath.Mul(UMath.Up, 0.95f));

            motor.Teleport(Ref.Pos(start));

            // The motor keeps velocity, sprint and slide state across cases, so
            // all three are cleared by standing still. Without this the walking
            // cases inherited sprinting from the sprint case before them.
            for (int i = 0; i < 60; i++)
                motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw) }, FixedDeltaTime);

            tick = 0;
            notGrounded = 0;
            streak = 0;
            worstStreak = 0;
        }

        protected override void FixedUpdate()
        {
            if (caseIndex >= Ramps.Length * 2)
                return;

            var input = new MoveInput
            {
                Move = new Vector2(0f, 1f),
                Yaw = Ref.Angle(yaw),
                Pitch = 0f,
                JumpPresses = 0,
                Sprint = sprint,
                Crouch = false,
            };

            motor.Tick(input, FixedDeltaTime);

            // Settling ticks are not part of the question.
            if (tick >= 20)
            {
                if (!motor.IsGrounded)
                {
                    notGrounded++;
                    streak++;
                    if (streak > worstStreak)
                        worstStreak = streak;
                }
                else
                {
                    streak = 0;
                }
            }

            tick++;
            if (tick < TicksPerCase)
                return;

            int counted = TicksPerCase - 20;
            Log($"[slope] == {Ramps[caseIndex / 2]}" +
                $" {(sprint ? "sprint" : "gehen")} runter:" +
                $" ohne Boden {notGrounded} von {counted}" +
                $" ({100f * notGrounded / counted:F0} %)," +
                $" längste Strecke {worstStreak} Ticks" +
                $" ({worstStreak * 0.025f:F3} s)");
            Next();
        }
    }
}
