using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Dummy;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Proves the simulation deterministic: a live run steered along a route
    /// records its inputs, then every replay - from the start, after a rollback,
    /// in reversed body order - must reproduce its state bit for bit. Bits, not
    /// a tolerance: a determinism test with a tolerance tests nothing.
    /// </summary>
    public sealed class DeterminismProbe : Probe
    {
        protected override string Tag => "det";

        const int Ticks = 1000;
        const int SettleTicks = 20;
        const int Seed = 20261015;
        const int RollbackTick = 100;
        const int CounterTick = 200;

        // Unity coordinates, mirrored at the motor.
        static readonly Vector3 Start0 = new Vector3(6f, 0.9f, -4f);

        // A route over everything the motor does: hitting and pushing the
        // dummy, sprint and slide, a flight of stairs, a ramp onto the plateau,
        // the drop off its edge and a ramp too steep to climb.
        struct Leg
        {
            public Vector2 Target;
            public int MaxTicks;
            public bool Sprint;
            public float AttackRate;
            public float Wiggle;
            public int SlideAt;      // ticks into the leg, -1 for none
            public bool Calm;        // no random jumps
        }

        static Leg L(float x, float z, int ticks, bool sprint = false,
                     float attack = 0f, float wiggle = 0f, int slideAt = -1,
                     bool calm = false)
            => new Leg
            {
                Target = new Vector2(x, z), MaxTicks = ticks, Sprint = sprint,
                AttackRate = attack, Wiggle = wiggle, SlideAt = slideAt,
                Calm = calm || attack > 0f,
            };

        static readonly Leg[] Route =
        {
            L(12f, -4f, 60, attack: 0.15f),
            L(13f, -4f, 30, attack: 0.5f),   // behind the dummy, never reached
            L(5f, -4f, 50),
            L(16f, -4f, 50, sprint: true, attack: 0.1f),
            L(8f, -9f, 50, sprint: true, slideAt: 20),
            L(-6f, -13f, 160, wiggle: 20f, calm: true),
            L(-6f, -3f, 100, wiggle: 40f, calm: true),
            L(-3.5f, -5f, 60),
            L(-3.5f, 12f, 120, sprint: true),
            // Off the -x edge, beside the steep ramp. The free +z edge lies 3 m
            // from the end of the ground, and a fall from it lands beyond.
            L(-3f, 20f, 80),
            L(-10f, 20f, 60),
            L(-12f, 16f, 60),
            L(-14f, 16f, 40),
            L(-6f, 16f, 80),
            L(0f, 0f, 200, sprint: true, wiggle: 30f),
        };

        SimulationSystem sim;
        PlayerMotor player;
        Entity playerEntity;
        Entity dummyEntity;
        ScriptedBody scripted;
        TrainingDummy dummy;
        MoveInput dummyInput;

        readonly MoveInput[] buffer = new MoveInput[2];
        readonly MoveInput[] inputs = new MoveInput[Ticks];
        readonly WorldSnapshot[] snaps = new WorldSnapshot[Ticks + 1];
        readonly ulong[] reference = new ulong[Ticks + 1];
        readonly bool[] contactAt = new bool[Ticks];

        // Transforms as the last tick published them, checked mid-tick.
        Vector3 publishedPlayer;
        Vector3 publishedDummy;
        int bufferChecks;
        int bufferMoved;     // ticks where the new pose really differed
        int bufferLeaks;

        Random rng;
        int tick = -SettleTicks;
        int leg;
        int legTick;
        int jumpHoldLeft;
        bool failed;

        protected override void Start()
        {
            Prototype proto = Prototype.Open(World);
            player = proto.Motor;
            playerEntity = proto.Player;
            dummyEntity = proto.Dummy;

            sim = World.Simulation;
            // The route climbs the plateau, so the round is part of every hash.
            World.StartMatch();
            dummy = dummyEntity.Get<TrainingDummy>();
            if (sim == null || dummy == null)
            {
                LogError("[det] SimulationSystem oder TrainingDummy fehlt");
                Exit(3);
                return;
            }

            // The probe drives Step itself, so it alone decides the inputs.
            // Inactive, Tick does nothing while Step still runs.
            sim.Deactivate();
            scripted = new ScriptedBody();
            playerEntity.Add(scripted);
            dummyInput = dummy.ReadInput();

            if (sim.Bodies.Count != 2)
            {
                LogError($"[det] {sim.Bodies.Count} Körper statt 2");
                Exit(3);
                return;
            }

            for (int i = 0; i < snaps.Length; i++)
                snaps[i] = new WorldSnapshot();

            rng = new Random(Seed);
            player.Teleport(Ref.Pos(Start0));
            sim.BeforePublish = CheckBuffer;
        }

        // Everyone has simulated, nobody has published: the transforms must
        // still show the last tick. Anything else means a body could have
        // seen another's half finished move.
        void CheckBuffer()
        {
            bufferChecks++;
            if (!Same(playerEntity.Transform.Position, publishedPlayer)
                || !Same(dummyEntity.Transform.Position, publishedDummy))
                bufferLeaks++;

            if (!Same(player.CaptureState().Position, publishedPlayer))
                bufferMoved++;
        }

        // Exact, unlike Unity's Vector3 ==, which forgives anything under 1e-5.
        static bool Same(Vector3 a, Vector3 b)
            => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

        void Remember()
        {
            publishedPlayer = playerEntity.Transform.Position;
            publishedDummy = dummyEntity.Transform.Position;
        }

        protected override void FixedUpdate()
        {
            if (scripted == null || failed)
                return;

            if (tick < 0)
            {
                StepWith(new MoveInput { Yaw = Ref.Angle(90f) });
                tick++;
                if (tick == 0)
                {
                    sim.Capture(snaps[0]);
                    reference[0] = Hash();
                }
                return;
            }

            if (tick < Ticks)
            {
                inputs[tick] = Steer();
                StepWith(inputs[tick]);
                contactAt[tick] = sim.LastContactCount > 0;
                tick++;
                sim.Capture(snaps[tick]);
                reference[tick] = Hash();
                return;
            }

            // All replays in one frame, with no physics step in between: the
            // situation a server resimulating a late input is in.
            if (PrintHashes)
                LogChain();

            bool ok = Report();
            Log(ok ? "[det] ALLE FAELLE DETERMINISTISCH" : "[det] FEHLER");
            Exit(ok ? 0 : 1);
            failed = true;
        }

        /// <summary>
        /// Prints the recorded hash chain, for comparing builds: the same
        /// run under JIT and NativeAOT must give the same chain. Off by
        /// default, so the log stays line for line Unity's.
        /// </summary>
        public bool PrintHashes { get; init; }

        void LogChain()
        {
            ulong chain = 14695981039346656037UL;
            foreach (ulong h in reference)
            {
                Mix(ref chain, (int)h);
                Mix(ref chain, (int)(h >> 32));
            }

            // The chain depends on the instruction set the code runs with.
            Log($"[det] Befehlssatz: AVX {System.Runtime.Intrinsics.X86.Avx.IsSupported}, " +
                $"AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}, " +
                $"FMA {System.Runtime.Intrinsics.X86.Fma.IsSupported}, " +
                $"Vector<float> {System.Numerics.Vector<float>.Count} breit");
            for (int t = 0; t <= Ticks; t += Ticks / 4)
                Log($"[det] Hash Tick {t}: {reference[t]:X16}");
            Log($"[det] Hashkette über {reference.Length} Ticks: {chain:X16}");
        }

        MoveInput Steer()
        {
            // Past the last leg the player stands still, the dummy keeps going.
            if (leg >= Route.Length)
                return new MoveInput { Yaw = inputs[tick - 1].Yaw };

            Leg l = Route[leg];
            Vector3 stride = playerEntity.Transform.Position;
            Vector3 p = Ref.ToUnity(stride);
            Vector2 to = l.Target - new Vector2(p.X, p.Z);

            float yaw = UMath.Atan2(to.X, to.Y) * UMath.Rad2Deg
                      + UMath.Sin(legTick * 0.6f) * l.Wiggle;

            var input = new MoveInput
            {
                Move = new Vector2(0f, 1f),
                Yaw = Ref.Angle(yaw),
                Pitch = Ref.Angle(10f),
                Sprint = l.Sprint,
            };

            if (l.SlideAt >= 0 && legTick >= l.SlideAt && legTick < l.SlideAt + 25)
            {
                input.Crouch = true;
                input.CrouchPressed = legTick == l.SlideAt;
            }

            // Random jumps, holds and swings on top, from a fixed seed. They
            // land in the recording like everything else. Calm legs skip the
            // jumps: an eye 2.8 m up sends every swing over the dummy, and a
            // jump carries the body over the stairs it is meant to climb.
            if (!l.Calm && jumpHoldLeft == 0 && rng.NextDouble() < 0.025)
            {
                input.JumpPresses = 1;
                jumpHoldLeft = rng.Next(1, 9);
            }
            input.JumpHeld = jumpHoldLeft > 0;
            if (jumpHoldLeft > 0)
                jumpHoldLeft--;

            if (rng.NextDouble() < l.AttackRate)
                input.AttackPresses = 1;

            legTick++;
            float distance = UMath.Magnitude(to);
            if (distance < 0.8f || legTick >= l.MaxTicks)
            {
                Log($"[det] Etappe {leg} endet bei Tick {tick}, Position {Ref.V(stride)}," +
                    $" Ziel {(distance < 0.8f ? "erreicht" : "nicht erreicht")}");
                leg++;
                legTick = 0;
            }

            return input;
        }

        void StepWith(in MoveInput playerInput)
        {
            Remember();
            IReadOnlyList<SimulationBody> bodies = sim.Bodies;
            for (int i = 0; i < bodies.Count; i++)
                buffer[i] = bodies[i] == scripted ? playerInput : dummyInput;

            sim.Step(buffer, FixedDeltaTime);
        }

        bool Report()
        {
            bool ok = Coverage();
            ok &= Complete();

            if (bufferLeaks == 0 && bufferMoved > Ticks / 2)
                Log($"[det] GLEICH   Puffer: in {bufferChecks} Ticks, davon {bufferMoved}" +
                    " mit Bewegung, stand kein Transform vor dem Veroeffentlichen" +
                    " schon auf dem neuen Stand");
            else
            {
                Log($"[det] ANDERS   Puffer: {bufferLeaks} von {bufferChecks} Ticks" +
                    $" zeigten den neuen Stand zu frueh, {bufferMoved} mit Bewegung");
                ok = false;
            }

            ok &= Replay("Nachspielen ab Start", 0);
            ok &= Replay($"Rollback bei Tick {RollbackTick}", RollbackTick);

            // Restore puts the recorded order back, so the dummy moves to
            // the end of the list only after it; the next restore undoes it.
            ok &= Replay("Koerperliste umgedreht", 0, () =>
            {
                sim.Unregister(dummy);
                sim.Register(dummy);
            });

            ok &= ReplayAt("Rollback im Gleiten", RollbackTick + 1,
                           t => snaps[t].StateAt(Index(t)).Motor.Sliding);
            ok &= ReplayAt("Rollback im Steigflug", RollbackTick + 1, t =>
            {
                MotorState m = snaps[t].StateAt(Index(t)).Motor;
                return !m.TouchingGround && m.Velocity.Y > 1f;
            });
            // From the start: the dummy is met first thing on the route.
            ok &= ReplayAt("Rollback vor einem Stoss", 1, t => contactAt[t]);
            ok &= ResimulateApi();
            ok &= TwoWorlds();
            ok &= CounterProbe();
            return ok;
        }

        // Where the player sits in a snapshot; it records the list order of
        // the tick it was taken in.
        int Index(int t)
        {
            WorldSnapshot s = snaps[t];
            for (int i = 0; i < s.Count; i++)
                if (s.BodyAt(i) == scripted)
                    return i;
            return -1;
        }

        bool ReplayAt(string name, int first, Func<int, bool> pick)
        {
            for (int t = first; t < Ticks - 1; t++)
                if (pick(t))
                    return Replay($"{name} (Tick {t})", t);

            Log($"[det] FEHLT    {name}: kein passender Tick in der Aufnahme");
            return false;
        }

        bool Replay(string name, int from, Action afterRestore = null)
        {
            sim.Restore(snaps[from]);
            afterRestore?.Invoke();
            if (Hash() != reference[from])
                return Fail(name, from, "schon direkt nach dem Restore");

            for (int t = from; t < Ticks; t++)
            {
                StepWith(inputs[t]);
                if (Hash() != reference[t + 1])
                    return Fail(name, t + 1, Diff(t + 1));
            }

            Log($"[det] GLEICH   {name}: {Ticks - from} Ticks bitgleich");
            return true;
        }

        bool ResimulateApi()
        {
            var ticks = new List<MoveInput[]>();
            for (int t = 0; t < Ticks; t++)
            {
                var row = new MoveInput[2];
                IReadOnlyList<SimulationBody> bodies = sim.Bodies;
                for (int i = 0; i < bodies.Count; i++)
                    row[i] = bodies[i] == scripted ? inputs[t] : dummyInput;
                ticks.Add(row);
            }

            sim.Resimulate(snaps[0], ticks, FixedDeltaTime);
            if (Hash() != reference[Ticks])
                return Fail("Resimulate", Ticks, Diff(Ticks));

            Log($"[det] GLEICH   Resimulate: Endzustand nach {Ticks} Ticks bitgleich");
            return true;
        }

        /// <summary>
        /// A second world in the same process, built the same way, stepped
        /// in turn with this one through the whole recording. Both must
        /// match it tick for tick: nothing of one world may leak into the
        /// other, which a host and its peers in one probe rely on.
        /// </summary>
        bool TwoWorlds()
        {
            using var second = new HeadlessWorld();
            Prototype proto = Prototype.Open(second);
            var player2 = new ScriptedBody();
            proto.Player.Add(player2);
            var dummy2 = proto.Dummy.Get<TrainingDummy>();
            SimulationSystem sim2 = second.Simulation;
            second.StartMatch();
            sim2.Deactivate();
            proto.Motor.Teleport(Ref.Pos(Start0));

            var row = new MoveInput[2];
            MoveInput dummy2Input = dummy2.ReadInput();
            void Step2(in MoveInput input)
            {
                IReadOnlyList<SimulationBody> bodies = sim2.Bodies;
                for (int i = 0; i < bodies.Count; i++)
                    row[i] = bodies[i] == player2 ? input : dummy2Input;
                sim2.Step(row, FixedDeltaTime);
            }

            for (int i = 0; i < SettleTicks; i++)
                Step2(new MoveInput { Yaw = Ref.Angle(90f) });

            sim.Restore(snaps[0]);
            if (Hash(sim2, player2, dummy2) != reference[0])
                return Fail("Zwei Welten", 0, "zweite Welt schon nach dem Einschwingen anders");

            for (int t = 0; t < Ticks; t++)
            {
                StepWith(inputs[t]);
                Step2(inputs[t]);
                if (Hash() != reference[t + 1])
                    return Fail("Zwei Welten", t + 1, "erste Welt: " + Diff(t + 1));
                if (Hash(sim2, player2, dummy2) != reference[t + 1])
                    return Fail("Zwei Welten", t + 1, "zweite Welt weicht ab");
            }

            Log($"[det] GLEICH   Zwei Welten im Wechsel: {Ticks} Ticks, beide bitgleich mit der Aufnahme");
            return true;
        }

        // The other direction: one degree of yaw in one tick must show. A hash
        // that stays equal here would pass every case above without meaning.
        bool CounterProbe()
        {
            sim.Restore(snaps[CounterTick]);
            for (int t = CounterTick; t < Ticks; t++)
            {
                MoveInput input = inputs[t];
                if (t == CounterTick)
                    input.Yaw += Ref.Angle(1f);
                StepWith(input);

                if (Hash() != reference[t + 1])
                {
                    Log($"[det] GLEICH   Gegenprobe: 1 Grad bei Tick {CounterTick}" +
                        $" weicht ab Tick {t + 1} ab, wie es muss");
                    return true;
                }
            }

            Log("[det] FEHLER   Gegenprobe: veraenderte Eingabe blieb unsichtbar");
            return false;
        }

        bool Fail(string name, int t, string detail)
        {
            Log($"[det] ANDERS   {name}: ab Tick {t} - {detail}");
            return false;
        }

        // Fields a snapshot deliberately leaves out, each with its reason.
        // world, hullParts and eyeLocal are new in the port: Unity had no
        // query object, kept the shape in the hull and the eye on a mount.
        static readonly string[] NotState =
        {
            "hull", "sweep", "headroomHits", "groundHits",   // parts and buffers
            "world", "hullParts",                             // wiring
            "maxStepHeight", "eyeLocal",                      // derived from the height
        };

        // A replay only notices a forgotten field if it happened to differ.
        // This notices it always: every runtime field of the motor has to
        // appear in MotorState, or be named above with a reason.
        bool Complete()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldInfo f in Fields(typeof(MotorState)))
                known.Add(f.Name);

            // Unity's [SerializeField] settings are public fields here.
            var missing = new List<string>();
            foreach (FieldInfo f in AllFields(typeof(PlayerMotor)))
            {
                if (f.IsDefined(typeof(DataMemberAttribute), false) || f.IsPublic)
                    continue;
                if (!known.Contains(f.Name) && Array.IndexOf(NotState, f.Name) < 0)
                    missing.Add(f.Name);
            }

            if (missing.Count == 0)
            {
                Log($"[det] GLEICH   Vollstaendigkeit: jedes Laufzeitfeld des Motors steht in MotorState");
                return true;
            }

            Log($"[det] FEHLT    Vollstaendigkeit: nicht in MotorState: {string.Join(", ", missing)}");
            return false;
        }

        // Proves the run went where the cases claim it did; a replay of a body
        // standing still would be deterministic and worthless.
        bool Coverage()
        {
            int sliding = 0, air = 0, sprint = 0, steep = 0, steps = 0, contacts = 0;
            float maxY = float.MinValue;

            for (int t = 1; t <= Ticks; t++)
            {
                MotorState m = snaps[t].StateAt(Index(t)).Motor;
                if (m.Sliding) sliding++;
                if (!m.TouchingGround) air++;
                if (m.Sprinting) sprint++;
                if (m.OnSteepGround) steep++;
                if (m.SteppedUp) steps++;
                if (contactAt[t - 1]) contacts++;
                maxY = UMath.Max(maxY, m.Position.Y);
            }

            float percent = 0f;
            WorldSnapshot last = snaps[Ticks];
            for (int i = 0; i < last.Count; i++)
                if (last.BodyAt(i) == dummy)
                    percent = last.StateAt(i).DamagePercent;

            Log($"[det] Abdeckung: gleiten {sliding}, Luft {air}, Sprint {sprint}," +
                $" steil {steep}, Stufen {steps}, Stoesse {contacts}," +
                $" Dummy-Prozent {percent:F1}, max y {maxY:F2}, Etappe {leg}/{Route.Length}");

            int held = 0;
            for (int t = 1; t <= Ticks; t++)
                if (snaps[t].Match.Holder != null)
                    held++;
            Log($"[det] Zone: {held} Ticks allein gehalten, Punkte-Ticks {last.StateAt(Index(Ticks)).ScoreTicks}");

            var attack = playerEntity.Get<MeleeAttack>();
            Log($"[det] Schlaege {attack.SwingCount}, Treffer {attack.HitCount}");

            bool ok = sliding > 0 && air > 0 && sprint > 0 && steep > 0
                   && steps > 0 && contacts > 0 && percent > 0f;
            if (!ok)
                Log("[det] ABDECKUNG FEHLT: mindestens ein Zustand kam nie vor");
            return ok;
        }

        // FNV-1a over the bits of every field of both bodies, found by
        // reflection: a field added to MotorState is covered without anyone
        // remembering to add it here.
        ulong Hash() => Hash(sim, scripted, dummy);

        static ulong Hash(SimulationSystem system, SimulationBody player, SimulationBody other)
        {
            ulong h = 14695981039346656037UL;
            Mix(ref h, system.CurrentTick);
            HashObject(ref h, player.CaptureState());
            HashObject(ref h, other.CaptureState());
            HashMatch(ref h, system.Match.State, player);
            return h;
        }

        // The round holds body references, which the reflection walk cannot
        // fold; they go in as who they are instead.
        static void HashMatch(ref ulong h, in MatchState m, SimulationBody player)
        {
            Mix(ref h, m.RoundTicks);
            Mix(ref h, m.Over ? 1 : 0);
            Mix(ref h, m.PauseTicks);
            Mix(ref h, m.Inside);
            Mix(ref h, Who(m.Winner, player));
            Mix(ref h, Who(m.Holder, player));
        }

        static int Who(SimulationBody body, SimulationBody player) => body == null ? 0 : body == player ? 1 : 2;

        int Who(SimulationBody body) => Who(body, scripted);

        static void Mix(ref ulong h, int bits)
        {
            for (int i = 0; i < 4; i++)
            {
                h ^= (byte)(bits >> (i * 8));
                h *= 1099511628211UL;
            }
        }

        static void HashObject(ref ulong h, object value)
        {
            switch (value)
            {
                case float f: Mix(ref h, BitConverter.SingleToInt32Bits(f)); return;
                case int i: Mix(ref h, i); return;
                case bool b: Mix(ref h, b ? 1 : 0); return;
            }

            foreach (FieldInfo f in Fields(value.GetType()))
                HashObject(ref h, f.GetValue(value));
        }

        // Every struct the hash and the diff walk through, kept by name for
        // the trimmer: NativeAOT has no fields left to reflect on otherwise.
        // A nested type missing here stops the probe instead of hashing nothing.
        static readonly Dictionary<Type, FieldInfo[]> Layouts = new Dictionary<Type, FieldInfo[]>();

        static DeterminismProbe()
        {
            Keep(typeof(BodyState));
            Keep(typeof(MotorState));
            Keep(typeof(Vector3));
            Keep(typeof(Quaternion));
        }

        static void Keep([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type t)
            => Layouts[t] = t.GetFields(BindingFlags.Instance | BindingFlags.Public);

        static FieldInfo[] Fields(Type t)
            => Layouts.TryGetValue(t, out FieldInfo[] fields)
                ? fields
                : throw new InvalidOperationException($"[det] kein Feldlayout für {t}");

        static FieldInfo[] AllFields(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields
                                        | DynamicallyAccessedMemberTypes.NonPublicFields)] Type t)
            => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // Names every field that differs from the recording, for both bodies.
        string Diff(int t)
        {
            var sb = new StringBuilder();
            WorldSnapshot s = snaps[t];
            for (int i = 0; i < s.Count; i++)
            {
                SimulationBody body = s.BodyAt(i);
                string who = body == scripted ? "Spieler" : "Dummy";
                DiffObject(sb, who, s.StateAt(i), body.CaptureState());
            }

            MatchState want = s.Match, got = sim.Match.State;
            if (want.RoundTicks != got.RoundTicks || want.Over != got.Over || want.PauseTicks != got.PauseTicks ||
                want.Inside != got.Inside || want.Winner != got.Winner || want.Holder != got.Holder)
                sb.Append($" Runde {want.RoundTicks}/{want.Inside}/{Who(want.Holder)}->{got.RoundTicks}/{got.Inside}/{Who(got.Holder)}");

            return sb.Length == 0 ? "nur CurrentTick" : sb.ToString();
        }

        static void DiffObject(StringBuilder sb, string path, object want, object got)
        {
            // Named in Unity's coordinates like the rest of the log. The
            // mirror only flips signs, so it changes no bit comparison.
            if (want is Vector3 wv)
            {
                want = Ref.ToUnity(wv);
                got = Ref.ToUnity((Vector3)got);
            }
            else if (want is Quaternion wq)
            {
                want = Ref.ToUnity(wq);
                got = Ref.ToUnity((Quaternion)got);
            }

            if (want is float || want is int || want is bool)
            {
                bool same = want is float a
                    ? BitConverter.SingleToInt32Bits(a)
                      == BitConverter.SingleToInt32Bits((float)got)
                    : want.Equals(got);
                if (!same)
                    sb.Append(want is float w
                        ? $" {path} {w:R}->{(float)got:R}"
                        : $" {path} {want}->{got}");
                return;
            }

            foreach (FieldInfo f in Fields(want.GetType()))
                DiffObject(sb, path + "." + f.Name, f.GetValue(want), f.GetValue(got));
        }
    }
}
