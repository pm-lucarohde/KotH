using System;
using KotH.Probes.Harness;
using KotH.Shared.Bots;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;

namespace KotH.Probes.Probes
{
    /// <summary>A hosted round on MountDestiny as the client runs it: HostSession, the founder's
    /// PeerSession over LocalTransport and eleven bots on their own thread. After a warm-up, the
    /// simulation, the sessions and the bots together must allocate nothing on any thread.</summary>
    public sealed class AllocProbe : Probe
    {
        protected override string Tag => "alloc";

        const double FrameMs = 8.0;
        const double WarmMs = 120000.0;     // the countdown ends at 60 s; its first moves warm up too
        static readonly double MeasureMs =
            double.TryParse(Environment.GetEnvironmentVariable("KOTH_ALLOC_SECONDS"), out double s) ? s * 1000.0 : 120000.0;
        const int Bots = 11, Seed = 20261124;

        public override int Run(HeadlessWorld unused, int maxTicks)
        {
            using var world = new HeadlessWorld();
            world.LoadArena(Maps.Default);
            world.StartMatch().Seed = Seed;
            world.Simulation.Deactivate();

            double now = 0.0;
            var host = new HostSession(new MemoryNetwork(Seed).CreateEndpoint(), now);
            // KOTH_ALLOC_PILOT: the founder flies as a bot through TryInput, as KOTH_SHOT_PILOT does in the client.
            PeerSession session = null;
            BotDirector director = null;
            bool pilot = Environment.GetEnvironmentVariable("KOTH_ALLOC_PILOT") != null;
            MoveInput Pilot()
            {
                if (session.LocalNetId == 0 || session.Simulation.CurrentTick < 40)
                    return default;
                if (!director.IsBot(session.LocalNetId))
                    director.Add(session.LocalNetId);
                return director.TryInput(session.LocalNetId, session.Simulation.CurrentTick + PeerSession.InputDelay,
                                         out MoveInput input) ? input : default;
            }
            session = new PeerSession(new HeadlessNetWorld(world), host.LocalTransport, pilot ? Pilot : Idle, "Host", founder: true, now);
            host.AttachFounder(session);
            HillMatch round = world.Simulation.Match;
            round.MinPlayers = 1;
            using BotDirector made = director = BotDirector.Create(world.Builder, world.Simulation, session, Seed);
            host.Bots = director;
            session.KnownInputs = director;
            for (int i = 0; i < Bots; i++)
                director.Add(host.AddBot($"Bot {i + 1}"));
            director.StartWorker();

            AllocMeter.Enabled = true;
            long started = Environment.TickCount64;
            long before = 0, gcBefore = 0, threadBefore = 0, window = 0, hostBytes = 0, sessionBytes = 0, pumpBytes = 0;
            int tickBefore = 0;
            for (bool measuring = false; now < WarmMs + MeasureMs; now += FrameMs)
            {
                if (!measuring && now >= WarmMs)
                {
                    measuring = true;
                    Log($"[alloc] measuring from {Uptime():F2} s of the process");
                    window = GC.GetTotalAllocatedBytes(precise: true);
                    for (int p = 0; p < (int)AllocMeter.Part.Count; p++)
                        AllocMeter.Take((AllocMeter.Part)p);
                    tickBefore = world.Simulation.CurrentTick;
                    gcBefore = GC.CollectionCount(0);
                    threadBefore = GC.GetAllocatedBytesForCurrentThread();
                    before = GC.GetTotalAllocatedBytes(precise: true);
                }
                long a = GC.GetAllocatedBytesForCurrentThread();
                host.Update(now);
                long b = GC.GetAllocatedBytesForCurrentThread();
                session.Update(now);
                long c = GC.GetAllocatedBytesForCurrentThread();
                director.Pump(1.0);
                if (measuring)
                {
                    hostBytes += b - a;
                    sessionBytes += c - b;
                    if (c - b > 0 && Environment.GetEnvironmentVariable("KOTH_ALLOC_WHERE") != null)
                        Log($"[alloc] session {c - b} B at tick {world.Simulation.CurrentTick}, round tick {round.State.RoundTicks}");
                    pumpBytes += GC.GetAllocatedBytesForCurrentThread() - c;
                }
                if (Environment.GetEnvironmentVariable("KOTH_ALLOC_WINDOWS") != null && now % 20000.0 < FrameMs)
                {
                    long all = GC.GetTotalAllocatedBytes(precise: true);
                    Log($"[alloc] {now / 1000:F0} s ({Environment.TickCount64 - started} ms real): {all - window} B in 20 s, phase {round.State.Phase}");
                    window = all;
                }
            }
            long total = GC.GetTotalAllocatedBytes(precise: true) - before;
            long thread = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
            Log($"[alloc] measured until {Uptime():F2} s of the process");
            int gcs = GC.CollectionCount(0) - (int)gcBefore;
            int ticks = world.Simulation.CurrentTick - tickBefore;

            Log($"[alloc] {ticks} ticks measured, phase {round.State.Phase}, stage {round.Stage}, {session.Rollbacks} rollbacks");
            Log($"[alloc] all threads {total} B, game thread {thread} B (host {hostBytes}, session {sessionBytes}, " +
                $"pump {pumpBytes}), bots {AllocMeter.Take(AllocMeter.Part.Bots)} B, gen0 collections {gcs}");
            bool ok = total == 0 && gcs == 0 && ticks > 0;
            Log(ok ? "[alloc] PASS" : "[alloc] FAIL: the round allocates");
            return ok ? 0 : 1;
        }

        static MoveInput Idle() => default;

        static double Uptime() => (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds;
    }
}
