using KotH.Probes.Harness;
using KotH.Shared.Dummy;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Measures the push between two bodies. Unlike the other probes it does not
    /// tick the motor itself: the push is computed inside SimulationSystem, so a
    /// probe that went around it would measure nothing at all. Throwaway probe.
    /// </summary>
    public sealed class ImpactProbe : Probe
    {
        protected override string Tag => "impact";

        struct Case
        {
            public string name;
            public Vector3 start;   // player transform position, not its feet
            public float yaw;
            public Vector2 move;
            public bool sprint;
            public bool crouch;
            public int ticks;
            public int settle;      // pushes before this tick are not counted
            public int trace;       // log every n-th tick, 0 switches it off
        }

        // Far from every other measurement: StepTest sits around z -10, CrouchTest
        // within z +-1.5, the mountain beyond z 10. The ground reaches z -25.
        static readonly Vector3 DummySpot = new Vector3(0f, 0.9f, -18f);

        static readonly Case[] Cases =
        {
            new Case { name = "Sprint frontal", start = new Vector3(-6f, 0.9f, -18f),
                       yaw = 90f, move = new Vector2(0f, 1f), sprint = true, ticks = 70 },
            new Case { name = "Gehen frontal", start = new Vector3(-4f, 0.9f, -18f),
                       yaw = 90f, move = new Vector2(0f, 1f), ticks = 70 },
            new Case { name = "Schleichen frontal", start = new Vector3(-2f, 0.9f, -18f),
                       yaw = 90f, move = new Vector2(0f, 1f), crouch = true, ticks = 70 },
            new Case { name = "Schräg versetzt", start = new Vector3(-4f, 0.9f, -18.5f),
                       yaw = 90f, move = new Vector2(0f, 1f), ticks = 70 },
            new Case { name = "Von oben drauf", start = new Vector3(0f, 4f, -18f),
                       yaw = 90f, ticks = 70 },
            new Case { name = "Ruhig draufstehen", start = new Vector3(0f, 2.74f, -18f),
                       yaw = 90f, ticks = 100, settle = 20 },
            new Case { name = "Dauerdruck", start = new Vector3(-2f, 0.9f, -18f),
                       yaw = 90f, move = new Vector2(0f, 1f), ticks = 150, trace = 10 },
        };

        PlayerMotor player;
        PlayerMotor dummy;
        TrainingDummy dummyBody;
        ScriptedBody scripted;

        int caseIndex = -1;
        int tick;
        int pushes;
        bool reported;

        protected override void Start()
        {
            Prototype proto = Prototype.Open(World);
            player = proto.Motor;

            if (World.Simulation == null)
            {
                LogError("[impact] kein SimulationSystem in der Szene");
                Exit(3);
                return;
            }

            TrainingDummy target = proto.Dummy?.Get<TrainingDummy>();
            if (target == null)
            {
                LogError("[impact] kein TrainingDummy in der Szene");
                Exit(3);
                return;
            }

            dummy = target.Motor;
            dummyBody = target;

            // The player has no input reader, so this is the only body on it
            // and the motor is still ticked exactly once. Joining the scene
            // registers it, as AddComponent's OnEnable did.
            scripted = new ScriptedBody { BeforeTick = Step };
            proto.Player.Add(scripted);

            // Reading in BeforeTick would be a tick late: inputs are gathered
            // before the contacts are resolved, so a push read there has already
            // had a full tick of damping applied to it.
            World.Simulation.AfterContacts = Report;

            // The dummy registered first, so it would tick BEFORE the player and
            // its velocity would already be a tick old when read below.
            // Re-registering moves it behind the player (OnDisable, OnEnable).
            World.Simulation.Unregister(dummyBody);
            World.Simulation.Register(dummyBody);

            Next();
        }

        /// <summary>
        /// Sets the input for the tick about to run. Called while the system
        /// gathers inputs, which is BEFORE the contacts - the push of this tick
        /// is read in Report instead.
        /// </summary>
        void Step()
        {
            if (caseIndex >= Cases.Length)
                return;

            Case c = Cases[caseIndex];

            if (c.trace > 0 && tick % c.trace == 0)
                Log($"[impact] {c.name} Tick {tick}:" +
                    $" Spieler x {Ref.ToUnity(player.Position).X:F3}" +
                    $" v {Show(Ref.ToUnity(player.Velocity))}," +
                    $" Dummy x {Ref.ToUnity(dummy.Position).X:F3}" +
                    $" v {Show(Ref.ToUnity(dummy.Velocity))}");

            scripted.Input = new MoveInput
            {
                Move = c.move,
                Yaw = Ref.Angle(c.yaw),
                Sprint = c.sprint,
                Crouch = c.crouch,
            };

            tick++;
            if (tick < c.ticks)
                return;

            Summarise(c);
            Next();
        }

        /// <summary>
        /// Runs right after the contacts of a tick, before anything moves. That
        /// is the one place a push shows up unmixed with the damping that the
        /// tick applies to it straight afterwards.
        /// </summary>
        void Report()
        {
            if (caseIndex >= Cases.Length)
                return;

            Case c = Cases[caseIndex];

            if (World.Simulation.LastContactCount > 0 && tick >= c.settle)
            {
                pushes++;

                if (!reported)
                {
                    reported = true;

                    Vector3 n = Ref.ToUnity(World.Simulation.LastNormal);
                    float approach = World.Simulation.LastApproach;

                    Vector3 vPlayer = Ref.ToUnity(player.Velocity);
                    Vector3 vDummy = Ref.ToUnity(dummy.Velocity);
                    float alongPlayer = UMath.Dot(vPlayer, n);
                    float alongDummy = UMath.Dot(vDummy, n);

                    // Separation must be 2 * bounce * approach, and the sum along
                    // the normal must survive the hit - that is the momentum.
                    Log($"[impact] {c.name}: erster Stoß bei Tick {tick}," +
                        $" Normale {Show(n)}, Annäherung {approach:F3} m/s," +
                        $" Spieler entlang n {alongPlayer:F3}," +
                        $" Dummy entlang n {alongDummy:F3}," +
                        $" Trennung {alongDummy - alongPlayer:F3}" +
                        $" (geteilt durch Annäherung" +
                        $" {(alongDummy - alongPlayer) / approach:F3}," +
                        $" soll 2*bounce)," +
                        $" Summe entlang n {alongPlayer + alongDummy:F3}," +
                        $" Spieler {Show(vPlayer)}, Dummy {Show(vDummy)}");
                }
            }
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

            Place(player, c.start);
            Place(dummy, DummySpot);

            tick = 0;
            pushes = 0;
            reported = false;

            scripted.Input = new MoveInput { Yaw = Ref.Angle(c.yaw) };
        }

        void Summarise(Case c)
        {
            Vector3 p = Ref.ToUnity(player.Position);
            Vector3 d = Ref.ToUnity(dummy.Position);
            Vector3 gap = UMath.Sub(d, p);

            Log($"[impact] == {c.name}: Stöße {pushes}," +
                $" Spieler ({p.X:F2}/{p.Y:F2}/{p.Z:F2})," +
                $" Dummy ({d.X:F2}/{d.Y:F2}/{d.Z:F2})," +
                $" waagerecht {UMath.Magnitude(new Vector2(gap.X, gap.Z)):F3} m," +
                $" senkrecht {gap.Y:F3} m");
        }

        /// <summary>
        /// Teleports a body and clears what it was doing. Velocity survives a
        /// teleport, so without this every case would start with the momentum of
        /// the one before it.
        /// </summary>
        static void Place(PlayerMotor motor, Vector3 at)
        {
            motor.Teleport(Ref.Pos(at));
            motor.AddVelocity(UMath.Neg(motor.Velocity), 0f);
        }

        // A Unity-space vector, formatted as Unity's probe did.
        static string Show(Vector3 v) => $"({v.X:F2}/{v.Y:F2}/{v.Z:F2})";
    }
}
