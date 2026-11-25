using System;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>The game modes' rules on the Test arena. "mighty": Mighty & High against solo, each in a fresh
    /// world: the walk 1.25 times as fast, the jump 1.5 times and the double jump 3 times as high, a cane's knock
    /// 3 times as far at the same height (Luca, 25.11.2026). "free": 1, 2, 3, Free's rules (ModesProbe.Free.cs).</summary>
    public sealed partial class ModesProbe : Probe
    {
        protected override string Tag => "modes";

        /// <summary>Which mode's rules: "mighty" or "free".</summary>
        public string Which = "mighty";

        HeadlessWorld world;
        int failures;

        struct Measured
        {
            public float Walk, Jump, DoubleJump, Knock, KnockUp, KnockWant;
        }

        public override int Run(HeadlessWorld host, int maxTicks)
        {
            if (Which == "free")
                return RunFree(host);
            if (Which != "mighty")
            {
                Log($"[modes] unknown: {Which}");
                return 64;
            }
            world = host;
            Measured solo = Measure(MatchMode.Solo);
            world = new HeadlessWorld();
            Measured mighty = Measure(MatchMode.Mighty);

            Check(Near(mighty.Walk / solo.Walk, 1.25f, 0.005f), $"walk {solo.Walk:F3} -> {mighty.Walk:F3} m/s (x{mighty.Walk / solo.Walk:F3})");
            Check(Near(mighty.Jump / solo.Jump, 1.5f, 0.01f), $"jump {solo.Jump:F3} -> {mighty.Jump:F3} m (x{mighty.Jump / solo.Jump:F3})");
            Check(Near(mighty.DoubleJump / solo.DoubleJump, 3f, 0.01f),
                  $"double jump {solo.DoubleJump:F3} -> {mighty.DoubleJump:F3} m (x{mighty.DoubleJump / solo.DoubleJump:F3})");
            Check(Near(solo.Knock, solo.KnockWant, 1e-4f) && Near(mighty.Knock, mighty.KnockWant, 1e-4f)
                  && Near(mighty.KnockWant * mighty.KnockWant / (solo.KnockWant * solo.KnockWant), 3f, 0.05f),
                  $"cane at 50 %: knock speed {solo.Knock:F3} -> {mighty.Knock:F3} m/s (3 times the distance: {mighty.KnockWant:F3})");
            Check(mighty.KnockUp == solo.KnockUp, $"cane at 50 %: rise speed the same ({solo.KnockUp:F3}, {mighty.KnockUp:F3})");

            Log(failures == 0 ? "[modes] PASSED" : $"[modes] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        Measured Measure(MatchMode mode)
        {
            world.LoadArena(Maps.Test);
            HillMatch match = world.StartMatch();
            match.Mode = mode;
            ScriptedBody a = Spawn("A", new Vector3(-2f, 0.9f, 5f), NetWorld.FirstPlayerId);
            ScriptedBody b = Spawn("B", new Vector3(30f, 0.9f, 5f), NetWorld.FirstPlayerId + 1);
            // A spawn ring would have placed them: back where the arena is open.
            Ticks(1);
            a.Motor.Teleport(new Vector3(-2f, 0.9f, 5f));
            b.Motor.Teleport(new Vector3(30f, 0.9f, 5f));
            Ticks(40);
            var m = new Measured();

            // Walking along +x for 2 s: full walk speed.
            a.Input.Yaw = 90f;
            a.Input.Move = new Vector2(0f, 1f);
            Ticks(80);
            Vector3 v = a.Motor.Velocity;
            m.Walk = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
            a.Input.Move = Vector2.Zero;
            Ticks(60);

            // A full jump, held: the rise to the top.
            float ground = a.Motor.Position.Y;
            a.Input.JumpPresses = 1;
            a.Input.JumpHeld = true;
            Ticks(1);
            a.Input.JumpPresses = 0;
            m.Jump = Rise(a, ground);
            a.Input.JumpHeld = false;
            Ticks(60);

            // Falling from 4 m, the double jump: the rise from where it fired.
            a.Motor.Teleport(new Vector3(-2f, 4f, 5f));
            Ticks(4);
            a.Input.JumpPresses = 1;
            Ticks(1);
            a.Input.JumpPresses = 0;
            Ticks(1);
            a.Input.JumpPresses = 1;
            float fired = a.Motor.Position.Y;
            Ticks(1);
            a.Input.JumpPresses = 0;
            m.DoubleJump = Rise(a, fired);
            Ticks(80);

            // The cane on B at 50 %, both standing: the knock's speed right after the hit.
            Vector3 ahead = UMath.Rotate(UMath.Euler(0f, a.Input.Yaw), UMath.Forward);
            b.Motor.Teleport(UMath.Add(a.Motor.Position, UMath.Mul(ahead, 1.5f)));
            Ticks(40);
            Log($"[modes] {HillMatch.ModeName(mode)}: walk {m.Walk:F3}, jump {m.Jump:F3}, double {m.DoubleJump:F3}");
            DamagePercent percent = b.Entity.Get<DamagePercent>();
            MeleeAttack cane = a.Entity.Get<MeleeAttack>();
            if (percent == null || cane == null)
            {
                Check(false, $"{HillMatch.ModeName(mode)}: B has a damage percent ({percent != null}), A a cane ({cane != null})");
                return m;
            }
            percent.Restore(50f);
            a.Input.Pitch = 0f;
            a.Input.AttackPresses = 1;
            Ticks(1);
            a.Input.AttackPresses = 0;
            m.Knock = b.Motor.KnockLeft;
            m.KnockUp = b.Motor.Velocity.Y;
            m.KnockWant = PlayerMotor.LaunchSpeed(cane.KnockbackDistance * 0.5f * ModeRules.For(mode).Knock,
                                                  b.Motor.AirAcceleration, SimulationSystem.TickSeconds);
            Check(cane.HitCount == 1, $"{HillMatch.ModeName(mode)}: the cane hit B");
            return m;
        }

        // The highest the body gets above from, ticking until it falls again.
        float Rise(ScriptedBody body, float from)
        {
            float top = body.Motor.Position.Y;
            for (int t = 0; t < 200 && (t < 2 || body.Motor.Velocity.Y > 0f); t++)
            {
                Ticks(1);
                top = Math.Max(top, body.Motor.Position.Y);
            }
            return top - from;
        }

        ScriptedBody Spawn(string name, Vector3 at, int netId)
        {
            var body = new ScriptedBody();
            Entity e = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, at, Quaternion.Identity, body, new DamagePercent());
            e.Name = name;
            body.NetId = netId;
            return body;
        }

        void Ticks(int n)
        {
            for (int i = 0; i < n; i++)
                world.Simulation.Tick();
        }

        static bool Near(float x, float want, float tolerance) => MathF.Abs(x - want) <= tolerance * MathF.Max(1f, MathF.Abs(want));

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[modes] {(ok ? "ok    " : "ERROR ")} {what}");
        }
    }
}
