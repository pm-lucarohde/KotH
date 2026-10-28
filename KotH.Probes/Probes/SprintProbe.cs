using KotH.Probes.Harness;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Measures the sprint state: what starts it, what keeps it, what ends it.
    /// Reports the wish speed and the distance actually covered side by side,
    /// because a blocked CharacterController keeps the first and loses the second.
    /// </summary>
    public sealed class SprintProbe : Probe
    {
        protected override string Tag => "sprint";

        enum Speed { Any, Zero, Walk, Sprint, Crouch, Blocked, Uphill }
        enum Where { Flat, Ramp20, Ramp35, Ramp45 }

        struct Phase
        {
            public string name;
            public int ticks;
            public bool sprint;
            public bool crouch;
            public Vector2 move;
            public bool push;         // a body push lands at the start of this phase
            public int trace;         // log the first n ticks of the phase
            public bool wantSprint;
            public Speed wantSpeed;
        }

        struct Case
        {
            public string name;
            public Where where;
            public bool wall;         // a wall 10 m ahead, clear of the run-up
            public Phase[] phases;
        }

        // Its own patch of ground far outside the scene, so no ramp, step or
        // dummy can reach into a measurement about flat ground. Unity coordinates.
        static readonly Vector3 Spot = new Vector3(-110f, 0.9f, -120f);

        static readonly Vector2 Fwd = new Vector2(0f, 1f);
        static readonly Vector2 Back = new Vector2(0f, -1f);
        static readonly Vector2 Zero = new Vector2(0f, 0f);

        static Phase P(string name, int ticks, bool sprint, bool crouch,
                       Vector2 move, bool wantSprint, Speed wantSpeed)
            => new Phase { name = name, ticks = ticks, sprint = sprint,
                           crouch = crouch, move = move,
                           wantSprint = wantSprint, wantSpeed = wantSpeed };

        static Phase T(string name, int ticks, bool sprint, bool crouch,
                       Vector2 move, bool wantSprint, Speed wantSpeed, int trace)
        {
            Phase p = P(name, ticks, sprint, crouch, move, wantSprint, wantSpeed);
            p.trace = trace;
            return p;
        }

        static readonly Case[] Cases =
        {
            new Case { name = "Tap once", phases = new[] {
                P("tap, 1 tick",       1, true,  false, Fwd, true, Speed.Any),
                P("key up, forward", 40, false, false, Fwd, true, Speed.Sprint),
            }},
            new Case { name = "Release ends", phases = new[] {
                P("tap + run-up",   40, true,  false, Fwd, true,  Speed.Sprint),
                P("stop",      30, false, false, Zero, false, Speed.Zero),
                P("forward again",     40, false, false, Fwd, false, Speed.Walk),
            }},
            new Case { name = "Holding never ends", phases = new[] {
                P("hold + run-up",   40, true, false, Fwd, true, Speed.Sprint),
                P("hold, stand",      30, true, false, Zero, true, Speed.Zero),
                P("hold, forward",    40, true, false, Fwd, true, Speed.Sprint),
            }},
            new Case { name = "Crouch ends", phases = new[] {
                P("tap + run-up",   40, true,  false, Fwd, true,  Speed.Sprint),
                P("crouch",              30, false, true,  Fwd, false, Speed.Crouch),
                P("stand up",          40, false, false, Fwd, false, Speed.Walk),
            }},
            new Case { name = "Crouch while holding", phases = new[] {
                P("hold + run-up",   40, true, false, Fwd, true, Speed.Sprint),
                P("hold + crouch",     30, true, true,  Fwd, true, Speed.Crouch),
                P("hold, stand up",  40, true, false, Fwd, true, Speed.Sprint),
            }},
            new Case { name = "Push ends", phases = new[] {
                P("tap + run-up",   40, true, false, Fwd, true, Speed.Sprint),
                // Two ticks: the limit reads the previous tick, so the push
                // lands in the first and ends the sprint in the second.
                new Phase { name = "push against", ticks = 2, move = Fwd,
                            push = true, wantSprint = false, wantSpeed = Speed.Any },
                P("then forward",     40, false, false, Fwd, false, Speed.Walk),
            }},
            new Case { name = "Wall, tapped", wall = true, phases = new[] {
                P("tap + run-up",   40, true,  false, Fwd, true,  Speed.Sprint),
                P("run into the wall",  40, false, false, Fwd, false, Speed.Blocked),
                P("back, forward",    40, false, false, Back, false, Speed.Walk),
            }},
            new Case { name = "Wall, held", wall = true, phases = new[] {
                P("hold + run-up",   40, true, false, Fwd, true, Speed.Sprint),
                P("run into the wall",  40, true, false, Fwd, true, Speed.Blocked),
                P("hold, back",      40, true, false, Back, true, Speed.Sprint),
            }},
            // The open question about measuring the real step: uphill the
            // horizontal part of it is shorter than the distance travelled.
            new Case { name = "Ramp 20 tapped", where = Where.Ramp20, phases = new[] {
                P("tap + run-up",   20, true,  false, Fwd, true, Speed.Any),
                P("ramp up",          30, false, false, Fwd, true, Speed.Uphill),
            }},
            new Case { name = "Ramp 35 tapped", where = Where.Ramp35, phases = new[] {
                P("tap + run-up",   20, true,  false, Fwd, true, Speed.Any),
                P("ramp up",          30, false, false, Fwd, true, Speed.Uphill),
            }},
            new Case { name = "Ramp 45 tapped", where = Where.Ramp45, phases = new[] {
                P("tap + run-up",   20, true,  false, Fwd, true, Speed.Any),
                P("ramp up",          30, false, false, Fwd, true, Speed.Uphill),
            }},
            // Held, so the sprint survives the impact tick and the steady
            // uphill step becomes measurable at all.
            new Case { name = "Ramp 20 held", where = Where.Ramp20, phases = new[] {
                P("run-up",            20, true, false, Fwd, true, Speed.Any),
                T("ramp up",          30, true, false, Fwd, true, Speed.Uphill, 14),
            }},
            new Case { name = "Ramp 35 held", where = Where.Ramp35, phases = new[] {
                P("run-up",            20, true, false, Fwd, true, Speed.Any),
                T("ramp up",          30, true, false, Fwd, true, Speed.Uphill, 14),
            }},
            new Case { name = "Ramp 45 held", where = Where.Ramp45, phases = new[] {
                P("run-up",            20, true, false, Fwd, true, Speed.Any),
                T("ramp up",          30, true, false, Fwd, true, Speed.Uphill, 14),
            }},
            new Case { name = "Wall held, trace", where = Where.Flat,
                       wall = true, phases = new[] {
                P("run-up",            40, true, false, Fwd, true, Speed.Any),
                T("into the wall",         40, true, false, Fwd, true, Speed.Blocked, 14),
            }},
        };

        PlayerMotor motor;
        Entity wall;

        float walkSpeed;
        float sprintSpeed;
        float crouchSpeed;

        bool ready;
        int caseIndex = -1;
        int phaseIndex;
        int tick;
        int failures;
        float yaw;
        float lastReal;           // 3D distance of the last tick over dt
        float lastFlat;           // its horizontal part, which uphill is smaller
        float minRealLate;        // slowest of the phase's final third
        int sprintLostAt = -1;    // tick inside the phase the state went out

        protected override void Start()
        {
            motor = Prototype.Open(World).Motor;

            // Read instead of written down: a target value that merely repeats
            // a guessed number stands there silently wrong after the next
            // tuning pass. Public fields here, so the compiler checks them.
            walkSpeed = motor.WalkSpeed;
            sprintSpeed = motor.SprintSpeed;
            crouchSpeed = motor.CrouchSpeed;

            CheckSlopeHeadroom();
            BuildFlatGround();

            Log($"[sprint] targets: walk {walkSpeed:F3}," +
                $" sprint {sprintSpeed:F3}, crouched {crouchSpeed:F3} m/s");

            // The first case waits for FixedUpdate, as it did in Unity, where
            // the fresh ground was not yet known to PhysX at this point.
            ready = true;
        }

        // Uphill the real step shrinks to sprintSpeed * cos(angle), so the
        // sprint only survives the steepest walkable ramp while walk over sprint
        // stays under cos(slopeLimit). Checked, not assumed: retuning either
        // speed or the slope limit can break it silently.
        void CheckSlopeHeadroom()
        {
            float ratio = walkSpeed / sprintSpeed;
            float limit = UMath.Cos(motor.SlopeLimit * UMath.Deg2Rad);

            Log($"[sprint] walk/sprint {ratio:F3} vs" +
                $" cos({motor.SlopeLimit:F0}°) = {limit:F3}:" +
                $" {(ratio < limit ? "room left" : "TOO TIGHT, sprint dies uphill")}");

            if (ratio >= limit)
                failures++;
        }

        void BuildFlatGround()
        {
            Arena.AddBox(World, "SprintProbeFloor",
                new Vector3(Spot.X, -0.5f, Spot.Z + 15f), new Vector3(40f, 1f, 60f));

            // 10 m ahead, so a 40 tick run-up at sprint speed still ends clear
            // of it and the collision is a measurement of its own. Created
            // active and switched off afterwards, as CreatePrimitive did.
            wall = Arena.AddBox(World, "SprintProbeWall",
                new Vector3(Spot.X, 1.5f, Spot.Z + 10f), new Vector3(12f, 3f, 0.5f));
            World.SetActive(wall, false);
        }

        void NextCase()
        {
            caseIndex++;
            if (caseIndex >= Cases.Length)
            {
                Log(failures == 0
                    ? "[sprint] ALL CASES AS EXPECTED"
                    : $"[sprint] {failures} DEVIATIONS");
                Exit(failures == 0 ? 0 : 1);
                return;
            }

            Case c = Cases[caseIndex];
            World.SetActive(wall, c.wall);
            Place(c.where);

            // The motor keeps velocity and sprint state across cases, so both
            // are cleared by standing still: no case may inherit the last one.
            for (int i = 0; i < 60; i++)
                motor.Tick(new MoveInput { Yaw = Ref.Angle(yaw) }, FixedDeltaTime);

            Log($"[sprint] == {c.name}");
            phaseIndex = 0;
            StartPhase();
        }

        // Placed once per case, never between phases: the phases run on without
        // a break, so a lost sprint cannot be an artefact of a teleport.
        void Place(Where where)
        {
            Vector3 start = Spot;
            yaw = 0f;

            if (where != Where.Flat)
            {
                UnityTransform t = Arena.Find(World, where.ToString());
                Vector3 surface = UMath.Add(t.position, UMath.Mul(t.up, t.localScale.Y * 0.5f));
                Vector3 uphill = UMath.Neg(UMath.Normalized(new Vector3(t.forward.X, 0f, t.forward.Z)));
                Vector3 foot = UMath.Add(surface, UMath.Mul(t.forward, t.localScale.Z * 0.5f));

                start = UMath.Add(foot, UMath.Mul(uphill, -4f));
                start.Y = 0.9f;
                yaw = UMath.Atan2(uphill.X, uphill.Z) * UMath.Rad2Deg;
            }

            motor.Teleport(Ref.Pos(start));
        }

        void StartPhase()
        {
            tick = 0;
            lastReal = 0f;
            lastFlat = 0f;
            minRealLate = float.MaxValue;
            sprintLostAt = -1;

            if (Cases[caseIndex].phases[phaseIndex].push)
            {
                // The same path a body contact takes: SimulationSystem calls
                // AddVelocity, nothing else. A frontal push at two thirds of the
                // speed lands below walk speed, which is what ends the sprint.
                Vector3 v = Ref.ToUnity(motor.Velocity);
                Vector3 flat = new Vector3(v.X, 0f, v.Z);
                motor.AddVelocity(Ref.Pos(UMath.Mul(UMath.Neg(flat), 0.667f)), 0.2f);
            }
        }

        protected override void FixedUpdate()
        {
            if (ready && caseIndex < 0)
            {
                ready = false;
                NextCase();
                return;
            }

            if (caseIndex < 0 || caseIndex >= Cases.Length)
                return;

            Phase p = Cases[caseIndex].phases[phaseIndex];
            float dt = FixedDeltaTime;

            Vector3 before = Ref.ToUnity(motor.Position);
            bool wasSprinting = motor.IsSprinting;

            motor.Tick(new MoveInput
            {
                Move = p.move,
                Yaw = Ref.Angle(yaw),
                Sprint = p.sprint,
                Crouch = p.crouch,
            }, dt);

            Vector3 after = Ref.ToUnity(motor.Position);
            Vector3 step = UMath.Sub(after, before);
            lastReal = UMath.Magnitude(step) / dt;
            lastFlat = UMath.Magnitude(new Vector2(step.X, step.Z)) / dt;

            if (wasSprinting && !motor.IsSprinting && sprintLostAt < 0)
                sprintLostAt = tick;

            if (tick >= p.ticks * 2 / 3 && lastReal < minRealLate)
                minRealLate = lastReal;

            if (tick < p.trace)
                Log($"[sprint]       tick {tick,3}: real {lastReal,6:F3}" +
                    $" {(lastReal < walkSpeed ? "BELOW" : "     ")}," +
                    $" horizontal {lastFlat,6:F3}," +
                    $" sprint {(motor.IsSprinting ? "ON " : "off")}," +
                    $" y {after.Y,6:F2}," +
                    $" ground {(motor.IsGrounded ? "yes " : "no  ")}");

            tick++;
            if (tick < p.ticks)
                return;

            Report(p);

            phaseIndex++;
            if (phaseIndex < Cases[caseIndex].phases.Length)
                StartPhase();
            else
                NextCase();
        }

        void Report(Phase p)
        {
            Vector3 v = Ref.ToUnity(motor.Velocity);
            float wish = UMath.Magnitude(new Vector2(v.X, v.Z));
            bool sprinting = motor.IsSprinting;
            Vector3 pos = Ref.ToUnity(motor.Position);

            float want;
            switch (p.wantSpeed)
            {
                case Speed.Walk: want = walkSpeed; break;
                case Speed.Sprint: want = sprintSpeed; break;
                case Speed.Crouch: want = crouchSpeed; break;
                case Speed.Blocked: want = 0f; break;
                case Speed.Zero: want = 0f; break;
                // Computed from the angle actually measured underfoot, so the
                // target follows the ramp instead of repeating a written number.
                case Speed.Uphill:
                    want = sprintSpeed
                         * UMath.Cos(motor.GroundAngle * UMath.Deg2Rad);
                    break;
                default: want = -1f; break;
            }

            float tolerance = p.wantSpeed == Speed.Blocked ? 0.2f : 0.05f;
            if (p.wantSpeed == Speed.Uphill)
                tolerance = 0.1f;
            bool speedOk = want < 0f || UMath.Abs(lastReal - want) < tolerance;
            bool ok = sprinting == p.wantSprint && speedOk;

            if (!ok)
                failures++;

            string wanted = $"sprint {(p.wantSprint ? "on" : "off")}" +
                            (want < 0f ? "" : $", real {want:F2}");

            if (p.wantSpeed == Speed.Uphill)
                wanted += $" (sprint x cos {motor.GroundAngle:F1}°)";

            Log($"[sprint]    {p.name,-20} after {p.ticks,3} ticks:" +
                $" sprint {(sprinting ? "ON " : "off")}" +
                $"{(sprintLostAt >= 0 ? $" (ended at tick {sprintLostAt,3})" : "")}," +
                $" wanted {wish,6:F3}, real {lastReal,6:F3}" +
                $" (horizontal {lastFlat,6:F3})" +
                $" (min last third {minRealLate,6:F3})," +
                $" x/y/z {pos.X,8:F2}/" +
                $"{pos.Y,6:F2}/{pos.Z,8:F2}" +
                $"  -> {(ok ? "ok" : "DEVIATION, expected " + wanted)}");
        }
    }
}
