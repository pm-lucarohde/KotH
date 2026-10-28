using KotH.Probes.Harness;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>Drives the motor against the stair flights with scripted input, so the
    /// wiggle exploit can be measured instead of guessed at. Ported from the Unity probe;
    /// cases, thresholds and log lines are unchanged.</summary>
    public sealed class StepProbe : Probe
    {
        protected override string Tag => "probe";

        struct Case
        {
            public string Name;
            public Vector3 Start;
            public Vector2 Move;
            public float BaseYaw;
            public float WiggleHz;
            public float WiggleDegrees;
            public bool Crouch;
        }

        // Flights sit at z = -10, 2 m wide: Steps20 x -6, Steps30 -10,
        // Steps40 -14, Steps50 -18. Steps50 spans x -19 to -17.
        static readonly Case[] Cases =
        {
            // Must climb.
            C("+ Steps20 stand",        -6f,   -11.5f, 0, 1, 0,   0,  0,   false),
            C("+ Steps20 stand wobble", -6f,   -11.5f, 0, 1, 0,   9f, 80f, false),
            C("+ Steps30 stand",        -10f,  -11.5f, 0, 1, 0,   0,  0,   false),
            C("+ Steps20 crouched",       -6f,   -11.5f, 0, 1, 0,   0,  0,   true),
            C("+ Steps20 crouched wobble",-6f,   -11.5f, 0, 1, 0,   9f, 80f, true),
            C("+ Steps40 still",         -14f,  -11.5f, 0, 1, 0,   0,  0,   false),
            C("+ Steps40 wobble",       -14f,  -11.5f, 0, 1, 0,   9f, 80f, false),
            C("+ Steps40 corner wobble",  -13.1f,-11.5f, 0, 1, 0,   9f, 80f, false),
            // Must NOT climb.
            C("- Steps50 still",         -18f,  -11.5f, 0, 1, 0,   0,  0,   false),
            C("- Steps50 wobble",       -18f,  -11.5f, 0, 1, 0,   9f, 80f, false),
            C("- Steps50 corner wobble",  -17.1f,-11.5f, 0, 1, 0,   9f, 80f, false),
            C("- Steps50 corner slow",  -17.1f,-11.5f, 0, 1, 0,   2f, 45f, false),
            C("- Steps50 sideways",      -17.1f,-9.0f,  1, 0, 0,   9f, 80f, false),
            C("- Steps30 crouched",       -10f,  -11.5f, 0, 1, 0,   9f, 80f, true),
            C("- Steps30 crouched corner",  -9.1f, -11.5f, 0, 1, 0,   9f, 80f, true),
            // Plateau top is y 5. Yaw 0 walks to its +z edge, the only one with
            // no ramp attached -- walking down a ramp is not falling off.
            L("= edge crouched",  new Vector3(0f, 5.95f, 16f), 0f, true),
            L("= edge standing",  new Vector3(0f, 5.95f, 16f), 0f, false),
        };

        // Same struct, but free to start anywhere: the ledge guard is tested on
        // a real drop, not on a flight of stairs. Positions are Unity's.
        static Case L(string name, Vector3 start, float yaw, bool crouch)
            => new Case
            {
                Name = name,
                Start = start,
                Move = new Vector2(0f, 1f),
                BaseYaw = yaw,
                WiggleHz = 0f,
                WiggleDegrees = 0f,
                Crouch = crouch,
            };

        static Case C(string name, float x, float z, float mx, float my,
                      float yaw, float hz, float deg, bool crouch)
            => new Case
            {
                Name = name,
                Start = new Vector3(x, 0.9f, z),
                Move = new Vector2(mx, my),
                BaseYaw = yaw,
                WiggleHz = hz,
                WiggleDegrees = deg,
                Crouch = crouch,
            };

        // Ramps are read from the arena instead of hard-coded: the object's
        // local +z points downhill, +y is the surface normal, scale is
        // (width, thickness, length). That is how they were constructed.
        struct RampCase
        {
            public string Name;
            public string Ramp;
            public bool AtFoot;   // start on flat ground below and walk uphill
            public bool Walk;
            public int Ticks;
        }

        static readonly RampCase[] RampCases =
        {
            R("Ramp45 stand",      "Ramp45", false, false, 80),
            R("Ramp55 stand",      "Ramp55", false, false, 80),
            R("Ramp45 walk up",   "Ramp45", true,  true,  240),
            R("Ramp55 walk up",   "Ramp55", true,  true,  240),
        };

        static RampCase R(string name, string ramp, bool atFoot, bool walk, int ticks)
            => new RampCase { Name = name, Ramp = ramp, AtFoot = atFoot, Walk = walk, Ticks = ticks };

        const int TicksPerCase = 240;

        PlayerMotor motor;

        // All positions below are Unity's; only the motor speaks Stride.
        int caseIndex = -1;
        int rampIndex = -1;
        int tick;
        float startY;
        float maxY;
        float minY;
        int steepTicks;
        Vector3 startFlat;
        int loggedJumps;
        int airTicks;        // no ground of any steepness under the feet
        int slideTicks;
        float maxAngle;
        Vector2 rampMove;
        float rampYaw;

        protected override void Start()
        {
            motor = Prototype.Open(World).Motor;

            Log("[probe] stepOffset " + motor.MaxStepHeight +
                ", height " + motor.Height +
                ", slopeLimit " + motor.SlopeLimit);

            // Ramps first: they are what is under investigation, and a watchdog
            // that fires must not eat the cases that matter.
            NextRampCase();
        }

        void NextCase()
        {
            caseIndex++;
            if (caseIndex >= Cases.Length)
            {
                Log("[probe] done");
                Exit(0);
                return;
            }

            Place(Cases[caseIndex].Start);
        }

        void NextRampCase()
        {
            rampIndex++;
            if (rampIndex >= RampCases.Length)
            {
                NextCase();
                return;
            }

            RampCase r = RampCases[rampIndex];
            UnityTransform t = Arena.Find(World, r.Ramp);

            // Surface instead of centre: the slab is thickness thick and its
            // pivot sits in the middle of it.
            Vector3 surface = UMath.Add(t.position, UMath.Mul(t.up, t.localScale.Y * 0.5f));
            Vector3 uphill = UMath.Neg(UMath.Normalized(new Vector3(t.forward.X, 0f, t.forward.Z)));
            rampYaw = UMath.Atan2(uphill.X, uphill.Z) * UMath.Rad2Deg;
            rampMove = r.Walk ? new Vector2(0f, 1f) : Vector2.Zero;

            Vector3 start;
            if (r.AtFoot)
            {
                Vector3 foot = UMath.Add(surface, UMath.Mul(t.forward, t.localScale.Z * 0.5f));
                start = UMath.Sub(foot, UMath.Mul(uphill, 1.5f));
                start.Y = 0.9f;
            }
            else
            {
                start = UMath.Add(surface, UMath.Mul(new Vector3(0f, 1f, 0f), 0.95f));
            }

            Log($"[probe] ~ {r.Name}: start {Ref.V(Ref.Pos(start))}, uphill yaw {rampYaw:F0}");
            Place(start);
        }

        // Generous: the deepest flight is climbed well inside this, but the
        // mountain is roughly 20 m away.
        bool NearStart(Vector3 p)
            => UMath.SqrMagnitude(UMath.Sub(new Vector3(p.X, 0f, p.Z), startFlat)) < 36f;

        void Place(Vector3 start)
        {
            motor.Teleport(Ref.Pos(start));

            tick = 0;
            startY = start.Y;
            startFlat = new Vector3(start.X, 0f, start.Z);
            maxY = startY;
            minY = startY;
            steepTicks = 0;
            loggedJumps = 0;
            airTicks = 0;
            slideTicks = 0;
            maxAngle = 0f;
        }

        protected override void FixedUpdate()
        {
            if (rampIndex < RampCases.Length)
            {
                RampTick();
                return;
            }

            if (caseIndex >= Cases.Length)
                return;

            Case c = Cases[caseIndex];

            float yaw = c.BaseYaw + (c.WiggleHz <= 0f
                ? 0f
                : UMath.Sin(tick * FixedDeltaTime * c.WiggleHz * UMath.PI * 2f) * c.WiggleDegrees);

            var input = new MoveInput
            {
                Move = c.Move,
                Yaw = Ref.Angle(yaw),
                Pitch = 0f,
                JumpPresses = 0,
                Sprint = false,
                Crouch = c.Crouch,
            };

            float before = motor.Position.Y;
            motor.Tick(input, FixedDeltaTime);
            float after = motor.Position.Y;

            if (motor.GroundAngle > motor.SlopeLimit)
                steepTicks++;

            // Only height gained near the flight counts. With wiggle the
            // player wanders tens of metres and walks up the mountain, which
            // says nothing about stairs -- that read as 4.59 m once.
            if (after > maxY && NearStart(Ref.ToUnity(motor.Position)))
                maxY = after;

            // Only the first few climbing ticks, otherwise the log drowns.
            if (after - before > 0.02f && loggedJumps < 6)
            {
                loggedJumps++;
                Log($"[probe] {c.Name} t{tick:D3} +{after - before:F3} m -> y {after:F3}" +
                    $" | angle {motor.GroundAngle:F1} | grounded {motor.IsGrounded}" +
                    $" | sliding {motor.IsSlidingDownSteep} | yaw {yaw:F0}");
            }

            tick++;
            if (tick >= TicksPerCase)
            {
                Vector3 end = Ref.ToUnity(motor.Position);
                Log($"[probe] == {c.Name}: max {maxY - startY:F3} m," +
                    $" end {end.Y - startY:F3} m," +
                    $" steep ticks {steepTicks}," +
                    $" end at x {end.X:F1} z {end.Z:F1}");
                NextCase();
            }
        }

        void RampTick()
        {
            RampCase r = RampCases[rampIndex];

            var input = new MoveInput
            {
                Move = rampMove,
                Yaw = Ref.Angle(rampYaw),
                Pitch = 0f,
                JumpPresses = 0,
                Sprint = false,
                Crouch = false,
            };

            motor.Tick(input, FixedDeltaTime);

            float y = motor.Position.Y;
            if (y > maxY) maxY = y;
            if (y < minY) minY = y;
            if (!motor.IsTouchingGround) airTicks++;
            if (motor.IsSlidingDownSteep) slideTicks++;
            if (motor.GroundAngle > maxAngle) maxAngle = motor.GroundAngle;
            if (motor.GroundAngle > motor.SlopeLimit) steepTicks++;

            // The first ticks tell the whole story: if isGrounded flickers, the
            // steep reading never holds long enough to confirm a slide.
            if (tick < 16)
                Log($"[probe] ~ {r.Name} t{tick:D3} y {y:F3}" +
                    $" | touching {motor.IsTouchingGround}" +
                    $" | angle {motor.GroundAngle:F1} | grounded {motor.IsGrounded}" +
                    $" | sliding {motor.IsSlidingDownSteep}");

            tick++;
            if (tick >= r.Ticks)
            {
                Log($"[probe] == {r.Name}: end {y - startY:+0.000;-0.000} m," +
                    $" max {maxY - startY:+0.000;-0.000}, min {minY - startY:+0.000;-0.000}," +
                    $" maxAngle {maxAngle:F1}, steep ticks {steepTicks}," +
                    $" slide ticks {slideTicks}, air ticks {airTicks} of {r.Ticks}");
                NextRampCase();
            }
        }
    }
}
