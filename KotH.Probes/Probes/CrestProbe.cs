using KotH.Probes.Harness;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Walks up a ramp from several lateral offsets and approach angles and
    /// reports whether the crest is cleared. "Sometimes stuck at the top" is a
    /// question about the approach, so one fixed path cannot answer it.
    /// </summary>
    public sealed class CrestProbe : Probe
    {
        protected override string Tag => "crest";

        const int TicksPerCase = 400;
        const float PlateauY = 5f;

        struct Case
        {
            public float Offset;    // sideways along the ramp width
            public float YawBias;   // degrees off straight uphill
            public bool Crouch;
            public bool Sprint;
            public float WiggleHz;  // view shaken while climbing
            public float WiggleDeg;
        }

        // Angles stay small: at 20 degrees the player simply walks off the
        // 4 m wide ramp, which says nothing about the crest.
        static readonly Case[] Cases =
        {
            K(0f, 0f, false, false), K(0f, 0f, false, true),
            K(-1.5f, 0f, false, true), K(1.5f, 0f, false, true),
            K(0f, 5f, false, false), K(0f, -5f, false, false),
            K(0f, 5f, false, true), K(0f, -5f, false, true),
            K(0f, 10f, false, false), K(0f, -10f, false, true),
            K(-1f, 8f, false, true), K(1f, -8f, false, true),
            W(0f, 0f, false, false, 6f, 30f), W(0f, 0f, false, true, 6f, 30f),
            W(0f, 0f, false, true, 11f, 60f), W(-1f, 0f, false, true, 11f, 60f),
            K(0f, 0f, true, false), K(0f, 5f, true, false),
        };

        static Case K(float offset, float yawBias, bool crouch, bool sprint)
            => new Case { Offset = offset, YawBias = yawBias,
                          Crouch = crouch, Sprint = sprint };

        static Case W(float offset, float yawBias, bool crouch, bool sprint,
                      float hz, float deg)
            => new Case { Offset = offset, YawBias = yawBias, Crouch = crouch,
                          Sprint = sprint, WiggleHz = hz, WiggleDeg = deg };

        PlayerMotor motor;

        int caseIndex = -1;
        int tick;
        float yaw;
        float maxY;
        float lastY;
        int reachedAt = -1;
        int stallTicks;    // high on the ramp, grounded, yet not rising
        int worstStall;

        protected override void Start()
        {
            motor = Prototype.Open(World).Motor;
            Next();
        }

        void Next()
        {
            caseIndex++;
            if (caseIndex >= Cases.Length)
            {
                Exit(0);
                return;
            }

            Case c = Cases[caseIndex];
            UnityTransform t = Arena.Find(World, "Ramp45");

            Vector3 surface = UMath.Add(t.position, UMath.Mul(t.up, t.localScale.Y * 0.5f));
            Vector3 uphill = UMath.Neg(UMath.Normalized(new Vector3(t.forward.X, 0f, t.forward.Z)));
            Vector3 side = UMath.Cross(new Vector3(0f, 1f, 0f), uphill);

            Vector3 foot = UMath.Add(surface, UMath.Mul(t.forward, t.localScale.Z * 0.5f));
            Vector3 start = UMath.Add(UMath.Add(foot, UMath.Mul(uphill, -1.5f)),
                                      UMath.Mul(side, c.Offset));
            start.Y = 0.9f;

            yaw = UMath.Atan2(uphill.X, uphill.Z) * UMath.Rad2Deg + c.YawBias;

            motor.Teleport(Ref.Pos(start));

            // The motor keeps velocity, sprint and slide state across cases, so
            // all three are cleared by standing still. Without this the crouched
            // case inherited sprinting from the case before and slid up instead.
            for (int i = 0; i < 60; i++)
                motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw) }, FixedDeltaTime);

            tick = 0;
            maxY = start.Y;
            lastY = start.Y;
            reachedAt = -1;
            stallTicks = 0;
            worstStall = 0;
        }

        protected override void FixedUpdate()
        {
            if (caseIndex >= Cases.Length)
                return;

            Case c = Cases[caseIndex];

            float shake = c.WiggleHz <= 0f ? 0f
                : UMath.Sin(tick * FixedDeltaTime * c.WiggleHz * UMath.PI * 2f)
                  * c.WiggleDeg;

            var input = new MoveInput
            {
                Move = new Vector2(0f, 1f),
                Yaw = Ref.Angle(yaw + shake),
                Pitch = 0f,
                JumpPresses = 0,
                Sprint = c.Sprint,
                Crouch = c.Crouch,
            };

            motor.Tick(input, FixedDeltaTime);

            float y = Ref.ToUnity(motor.Position).Y;
            if (y > maxY)
                maxY = y;

            bool onPlateau = y - 0.9f >= PlateauY - 0.1f;
            if (onPlateau && reachedAt < 0)
                reachedAt = tick;

            // A stall is standing high on the ramp, in contact, and not
            // rising. Plain climbing is not a stall, however slow it is.
            if (!onPlateau && y - 0.9f > 3.5f
                && motor.IsGrounded && y - lastY < 0.005f)
            {
                stallTicks++;
                if (stallTicks > worstStall)
                    worstStall = stallTicks;
            }
            else if (!onPlateau)
            {
                stallTicks = 0;
            }

            lastY = y;

            tick++;
            if (tick < TicksPerCase)
                return;

            string how = c.Crouch ? "crouched" : c.Sprint ? "sprint " : "walk   ";
            string shakeTag = c.WiggleHz > 0f ? $" wobble {c.WiggleDeg:F0}°" : "";
            Log($"[crest] == offset {c.Offset,5:F2} angle {c.YawBias,4:F0}" +
                $" {how}{shakeTag}:" +
                $" {(reachedAt >= 0 ? $"top at tick {reachedAt,3}" : "NEVER ON TOP")}," +
                $" max {maxY - 0.9f:F3} m," +
                $" longest stall {worstStall} ticks");
            Next();
        }
    }
}
