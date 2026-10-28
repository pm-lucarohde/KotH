using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using KotH.Shared.Bots;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Server
{
    /// <summary>
    /// The server as a networked peer, for tests over real UDP:
    ///
    ///   --host [--port N] [--min-players N]   hosts a game; its own player stands still
    ///   --join ADDRESS[:PORT]                 joins one
    ///   --bot                                 the own player roams at random instead
    ///   --bots N, --bot-seed N                the host's bots (3 by default) and their seed
    ///   --name NAME, --seconds N              shown name; stops after N seconds
    ///   --map NAME                            the host's map (Maps.Default); joiners take the host's
    ///
    /// Logs the round every 10 s and every 400th tick's checksum, so the
    /// logs of all processes can be compared line by line.
    /// </summary>
    static class NetPlay
    {
        public static bool Wanted(string[] args)
            => Array.IndexOf(args, "--host") >= 0 || Array.IndexOf(args, "--join") >= 0;

        public static int Run(string[] args)
        {
            bool hosting = Array.IndexOf(args, "--host") >= 0;
            string join = Text(args, "--join");
            int port = (int)(Number(args, "--port") ?? LiteNetTransport.DefaultPort);
            string name = Text(args, "--name") ?? (hosting ? "Host" : "Guest");
            double? seconds = Number(args, "--seconds");
            bool bot = Array.IndexOf(args, "--bot") >= 0;
            int minPlayers = (int)(Number(args, "--min-players") ?? HillMatch.DefaultMinPlayers);
            int bots = Math.Clamp((int)(Number(args, "--bots") ?? 3), 0, NetWorld.MaxPlayers - 1);
            int botSeed = (int)(Number(args, "--bot-seed") ?? 1);
            string map = Text(args, "--map") ?? Maps.Default;

            using var world = new HeadlessWorld();
            // A joiner builds the host's map once it is welcomed, see HeadlessNetWorld.
            if (hosting)
            {
                if (!world.Builder.HasMap(map))
                {
                    Console.WriteLine($"[net] unknown map: {map}");
                    return 1;
                }
                world.LoadArena(map);
                world.StartMatch().Seed = (uint)Random.Shared.Next();
            }
            world.Simulation.Deactivate();

            var wall = Stopwatch.StartNew();
            double Now() => wall.Elapsed.TotalMilliseconds;
            var roamer = new Roamer(name);
            Func<MoveInput> sample = bot ? roamer.Next : () => default;

            LiteNetTransport transport;
            HostSession host = null;
            PeerSession session;
            if (hosting)
            {
                transport = LiteNetTransport.Host(port);
                host = new HostSession(transport, Now());
                session = new PeerSession(new HeadlessNetWorld(world), host.LocalTransport, sample, name, founder: true, Now());
                host.AttachFounder(session);
                HillMatch round = world.Simulation.Match;
                round.MinPlayers = minPlayers;
                host.CanJoin = () => round.AcceptsJoin;
                Console.WriteLine($"[net] host on port {port}, map {map}, round from {minPlayers} players, build {NetProtocol.BuildText}");
                if (bots > 0)
                {
                    BotDirector director = BotDirector.Create(world.Builder, world.Simulation, session, botSeed);
                    host.Bots = director;
                    session.KnownInputs = director;
                    for (int i = 0; i < bots; i++)
                        director.Add(host.AddBot($"Bot {i + 1}"));
                    Console.WriteLine($"[net] {bots} bots, seed {botSeed}");
                }
            }
            else
            {
                (string address, int at) = Address(join, port);
                transport = LiteNetTransport.Join(address, at);
                session = new PeerSession(new HeadlessNetWorld(world), transport, sample, name, founder: false, Now());
                Console.WriteLine($"[net] {name} connects to {address}:{at}, build {NetProtocol.BuildText}");
            }

            session.Checksum += (tick, hash) =>
            {
                if (tick % 400 == 0)
                    Console.WriteLine($"[net] checksum tick {tick}: {hash:X16}");
            };

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            bool joined = false;
            double lastReport = 0;
            int code = 0;
            using (transport)
            {
                while (!stop.IsCancellationRequested)
                {
                    double now = Now();
                    if (seconds.HasValue && now >= seconds.Value * 1000.0)
                        break;

                    host?.Update(now);
                    session.Update(now);

                    if (session.Rejected is { } why)
                    {
                        Console.WriteLine($"[net] rejected: {why}");
                        code = 2;
                        break;
                    }
                    if (session.HostLost)
                    {
                        Console.WriteLine($"[net] host gone ({transport.DisconnectReason}), game over");
                        code = 3;
                        break;
                    }
                    if (!joined && session.Joined)
                    {
                        joined = true;
                        Console.WriteLine($"[net] joined as NetId {session.LocalNetId} at tick {world.Simulation.CurrentTick}, map {world.Builder.Map}");
                    }
                    if (now - lastReport >= 10000.0)
                    {
                        lastReport = now;
                        Report(world, session, host);
                    }

                    Thread.Sleep(2);
                }
            }

            Report(world, session, host);
            Console.WriteLine("[net] stopped");
            return code;
        }

        static void Report(HeadlessWorld world, PeerSession session, HostSession host)
        {
            MatchState m = world.Simulation.Match?.State ?? default;
            int players = HillMatch.CountPlayers(world.Simulation.Bodies);
            string phase = m.Phase switch
            {
                MatchPhase.Waiting => $"waiting ({players} / {m.MinPlayers})",
                MatchPhase.Countdown => $"Start in {(m.PhaseTicks + 39) / 40} s",
                MatchPhase.Over => "round over",
                _ => $"round {m.RoundTicks / 40} s",
            };
            string extra = host != null ? $", substituted {host.Substituted}, clamped {host.Sanitized}, desyncs {host.Desyncs}" : "";
            Console.WriteLine($"[net] Tick {world.Simulation.CurrentTick}, {players} players, {phase}, ping {session.RoundTripMs:F0} ms, " +
                              $"rollbacks {session.Rollbacks} (deepest {session.DeepestRollback}), stalls {session.Stalls}, " +
                              $"repairs {session.Repairs}{extra}");
        }

        static (string, int) Address(string text, int port)
        {
            // "host:port", but an IPv6 address has colons of its own.
            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int p))
                return (text[..colon], p);
            return (text, port);
        }

        static string Text(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static double? Number(string[] args, string name)
            => double.TryParse(Text(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

        /// <summary>Random keys and a mouse-like turn, held a while each, seeded by the name.</summary>
        sealed class Roamer
        {
            readonly Random random;
            MoveInput held;
            float turn;
            int ticks;

            public Roamer(string name)
            {
                int seed = 17;
                foreach (char ch in name)
                    seed = seed * 31 + ch;
                random = new Random(seed);
            }

            public MoveInput Next()
            {
                if (--ticks <= 0)
                {
                    ticks = 10 + random.Next(40);
                    turn = random.Next(3) == 0 ? 0f : (float)(random.NextDouble() * 8.0 - 4.0);
                    held = new MoveInput
                    {
                        Move = new Vector2(random.Next(3) - 1, random.Next(3) - 1),
                        Yaw = held.Yaw,
                        Pitch = (float)(random.NextDouble() * 40.0 - 20.0),
                        Sprint = random.Next(3) == 0,
                        Crouch = random.Next(12) == 0,
                    };
                }

                held.Yaw += turn;
                MoveInput m = held;
                if (random.Next(30) == 0)
                {
                    m.JumpPresses = 1;
                    m.JumpHeld = true;
                }
                if (random.Next(25) == 0)
                    m.AttackPresses = 1;
                return m;
            }
        }
    }
}
