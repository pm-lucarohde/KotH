using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>"rules": the host's lobby settings (Luca, 25.11.2026). The start damage, the percent each attack adds,
    /// the knockback factor; the lobby counting people only, starting by hand, Start pressed, and 1, 2, 3, Free
    /// without bots waiting for five.</summary>
    public sealed partial class ModesProbe
    {
        int RunRules(HeadlessWorld host)
        {
            world = host;
            StartAndHits();
            world = new HeadlessWorld();
            Lobby();
            Log(failures == 0 ? "[modes] PASSED" : $"[modes] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        void StartAndHits()
        {
            world.LoadArena(Maps.Test);
            HillMatch round = world.StartMatch();
            ScriptedBody plain = Spawn("Plain", new Vector3(-12f, 0.9f, 12f), NetWorld.FirstPlayerId);
            Ticks(1);
            Check(plain.Entity.Get<DamagePercent>().Percent == 0f, "unset: a solo round starts at 0 %");
            Check(round.Percent(HitKind.Spore, 1.2f) == 1.2f && round.Knock == 1f, "unset: each attack's own percent, knockback x1");

            round.Rules = new RoundRules { Set = true, StartPercent = 40f, Melee = 2.5f, Spore = 3f, Dash = 0.5f, Knock = 2f };
            ScriptedBody a = Spawn("A", new Vector3(-2f, 0.9f, 5f), NetWorld.FirstPlayerId + 1);
            ScriptedBody b = Spawn("B", new Vector3(30f, 0.9f, 5f), NetWorld.FirstPlayerId + 2);
            Ticks(1);
            a.Motor.Teleport(new Vector3(-2f, 0.9f, 5f));
            Check(a.Entity.Get<DamagePercent>().Percent == 40f && b.Entity.Get<DamagePercent>().Percent == 40f,
                  $"joining the round: start damage 40 % ({a.Entity.Get<DamagePercent>().Percent}, {b.Entity.Get<DamagePercent>().Percent})");
            Check(round.Percent(HitKind.Spore, 1.2f) == 3f && round.Percent(HitKind.Dash, 1.2f) == 0.5f,
                  "spore +3 %, dash +0.5 % instead of their own");

            // The cane on B at 50 %: +2.5 %, the knock twice as far.
            a.Input.Yaw = 90f;
            Ticks(40);
            Vector3 ahead = UMath.Rotate(UMath.Euler(0f, a.Input.Yaw), UMath.Forward);
            b.Motor.Teleport(UMath.Add(a.Motor.Position, UMath.Mul(ahead, 1.5f)));
            Ticks(40);
            DamagePercent percent = b.Entity.Get<DamagePercent>();
            MeleeAttack cane = a.Entity.Get<MeleeAttack>();
            percent.Restore(50f);
            a.Input.Pitch = 0f;
            a.Input.AttackPresses = 1;
            Ticks(1);
            a.Input.AttackPresses = 0;
            float want = PlayerMotor.LaunchSpeed(cane.KnockbackDistance * 0.5f * 2f, b.Motor.AirAcceleration, SimulationSystem.TickSeconds);
            Check(cane.HitCount == 1 && percent.Percent == 52.5f, $"the cane adds 2.5 % (50 -> {percent.Percent}, {cane.HitCount} hit)");
            Check(Near(b.Motor.KnockLeft, want, 1e-4f), $"knockback x2: speed {b.Motor.KnockLeft:F3}, for twice the distance {want:F3}");
        }

        void Lobby()
        {
            world.LoadArena(Maps.Test);
            HillMatch lobby = world.StartMatch();
            lobby.Stage = MatchStage.Lobby;
            lobby.MinPlayers = 2;
            Spawn("Host", new Vector3(-2f, 0.9f, 5f), NetWorld.FirstPlayerId);
            for (int i = 0; i < 3; i++)
                Spawn($"Bot {i + 1}", new Vector3(2f + 3f * i, 0.9f, 5f), NetWorld.FirstPlayerId + 1 + i).Bot = true;
            Ticks(2);
            Check(lobby.State.Phase == MatchPhase.Waiting, $"one person and three bots wait for two people ({lobby.State.Phase})");
            Spawn("Guest", new Vector3(-5f, 0.9f, 5f), NetWorld.FirstPlayerId + 4);
            Ticks(2);
            Check(lobby.State.Phase == MatchPhase.Countdown, $"two people count down ({lobby.State.Phase})");

            var c = new LobbyCommand { Mode = MatchMode.Solo, MinPlayers = 2, ManualStart = true };
            lobby.Command(c);
            Ticks(2);
            Check(lobby.State.Phase == MatchPhase.Waiting, $"auto start off: back to waiting ({lobby.State.Phase})");
            c.Start = true;
            lobby.Command(c);
            Ticks(2);
            Check(lobby.State.Phase == MatchPhase.Countdown && lobby.State.Forced, $"Start pressed: counts down ({lobby.State.Phase})");
            c.Start = false;
            lobby.Command(c);
            Ticks(2);
            Check(lobby.State.Phase == MatchPhase.Countdown, "the countdown Start began goes on");

            lobby.MinPlayers = 1;   // a fresh wait
            lobby.Command(new LobbyCommand { Mode = MatchMode.Free, MinPlayers = 1, BotsOff = true, Start = true });
            Ticks(2);
            Check(lobby.State.Phase == MatchPhase.Countdown && lobby.Mode == MatchMode.Free, "1, 2, 3, Free with five bodies starts");
            lobby.MinPlayers = 1;
            var bots = new System.Collections.Generic.List<SimulationBody>();
            foreach (SimulationBody body in world.Simulation.Bodies)
                if (body.Bot)
                    bots.Add(body);
            foreach (SimulationBody body in bots)
                world.Simulation.Leave(body);
            lobby.Command(new LobbyCommand { Mode = MatchMode.Free, MinPlayers = 1, BotsOff = true, Start = true });
            Ticks(2);
            Check(lobby.State.Phase == MatchPhase.Waiting, $"1, 2, 3, Free without bots and two people waits, Start or not ({lobby.State.Phase})");
        }
    }
}
