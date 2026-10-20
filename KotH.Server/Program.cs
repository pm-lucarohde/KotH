using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using KotH.Server;
using KotH.Shared.Hosting;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

// The headless host: the arena, the training dummy and the 40 Hz tick, no
// window and no GPU. Players and netcode come later; the loop is the one a
// peer acting as host will run.
//
//   KotH.Server                 runs in real time until Ctrl+C
//   KotH.Server --seconds N     runs in real time for N seconds
//   KotH.Server --ticks N       runs N ticks as fast as it can, then prints
//                               the timing and every body's exact state
//   --bodies N                  adds N roaming BenchBots (load test)
//   --warmup N                  leaves the first N fast ticks out of the timing

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("de-DE");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

double? seconds = Option(args, "--seconds");
double? fastTicks = Option(args, "--ticks");
int botCount = (int)(Option(args, "--bodies") ?? 0);
int warmup = (int)(Option(args, "--warmup") ?? 0);

using var world = new HeadlessWorld();
world.LoadArena();

Entity spawn = world.Find("DummySpawn");
Entity dummy = world.SpawnBody(HeadlessWorld.DummyBodyUrl, spawn.Transform.Position, spawn.Transform.Rotation);
dummy.Name = "Dummy";

// A grid on the open ground behind the stairs, all facing the mountain.
Entity playerSpawn = world.Find("PlayerSpawn");
for (int i = 0; i < botCount; i++)
{
    var at = new Vector3(-21f + (i % 16) * 2.8f, playerSpawn.Transform.Position.Y, 14f + (i / 16) * 2.5f);
    Entity bot = world.SpawnBody(HeadlessWorld.PlayerBodyUrl, at, Quaternion.Identity, new BenchBot(i + 1, at, 0f));
    bot.Name = "Bot" + i;
}

Console.WriteLine($"[server] Arena geladen, {SimulationSystem.Bodies.Count} Körper, {1.0 / TickClock.TickSeconds:F0} Hz");

var timing = new TickTiming((int)(fastTicks ?? 0));

if (fastTicks.HasValue)
{
    for (int i = 0; i < warmup; i++)
        world.Simulation.Tick();
    string traced = Environment.GetEnvironmentVariable("KOTH_TRACE");
    for (int i = 0; i < (int)fastTicks.Value; i++)
    {
        timing.Tick(world.Simulation);
        if (traced != null && world.Find(traced)?.Get<PlayerMotor>() is { } m)
        {
            MotorState t = m.CaptureState();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[spur] {0} pos {1:F4} {2:F4} {3:F4} v {4:F3} {5:F3} {6:F3} g {7} t {8} c {9} s {10} h {11:F3} w {12} st {13} kb {14:F3} kontakte {15}",
                SimulationSystem.CurrentTick, t.Position.X, t.Position.Y, t.Position.Z, t.Velocity.X, t.Velocity.Y, t.Velocity.Z,
                t.Grounded ? 1 : 0, t.TouchingGround ? 1 : 0, t.IsCrouching ? 1 : 0, t.Sliding ? 1 : 0, t.CurrentHeight,
                t.Wedged ? 1 : 0, t.SteppedUp ? 1 : 0, t.KnockbackTime, SimulationSystem.LastContactCount));
        }
    }

    timing.Report();
    timing.ReportSpread();


    // Every body's exact end state, folded into one FNV-1a hash so runs of
    // different builds can be compared; listed one by one for a few bodies.
    ulong hash = 14695981039346656037UL;
    int fallen = 0;
    foreach (SimulationBody body in SimulationSystem.Bodies)
    {
        MotorState s = body.Entity.Get<PlayerMotor>().CaptureState();
        foreach (float f in new[] { s.Position.X, s.Position.Y, s.Position.Z, s.Velocity.X, s.Velocity.Y, s.Velocity.Z })
            hash = (hash ^ BitConverter.SingleToUInt32Bits(f)) * 1099511628211UL;
        if (s.Position.Y < -1f && fallen++ < 3)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[server] {0} unter der Arena: Position {1:F1} {2:F1} {3:F1}, Ziel {4}",
                body.Entity.Name, s.Position.X, s.Position.Y, s.Position.Z, (body as BenchBot)?.Target));
        if (SimulationSystem.Bodies.Count <= 4)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[server] {0}: Position {1:R} {2:R} {3:R}, Tempo {4:R} {5:R} {6:R}",
                body.Entity.Name, s.Position.X, s.Position.Y, s.Position.Z, s.Velocity.X, s.Velocity.Y, s.Velocity.Z));
    }
    int respawns = 0;
    foreach (SimulationBody body in SimulationSystem.Bodies)
    {
        respawns += (body as BenchBot)?.Respawns ?? 0;
        if (body is BenchBot b && Environment.GetEnvironmentVariable("KOTH_FALLS") != null)
            foreach (Vector3 f in b.Falls)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "[fall] {0} {1:F2} {2:F2} {3:F2}", body.Entity.Name, f.X, f.Y, f.Z));
    }
    Console.WriteLine($"[server] Endzustand {hash:X16}, {fallen} von {SimulationSystem.Bodies.Count} Körpern unter der Arena, " +
                      $"{respawns} Bots zurückgesetzt");
    return 0;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var clock = new TickClock();
var wall = Stopwatch.StartNew();
double last = 0, lastReport = 0;

while (!stop.IsCancellationRequested)
{
    double now = wall.Elapsed.TotalSeconds;
    if (seconds.HasValue && now >= seconds.Value)
        break;

    int due = clock.Advance(now - last);
    last = now;
    for (int i = 0; i < due; i++)
        timing.Tick(world.Simulation);

    if (now - lastReport >= 10.0)
    {
        lastReport = now;
        timing.Report();
    }

    // Sleeps to the next tick instead of spinning; the scheduler's
    // millisecond is well inside a 25 ms tick.
    double wait = TickClock.TickSeconds - clock.SinceTick;
    if (wait > 0.0)
        Thread.Sleep(TimeSpan.FromSeconds(wait));
}

timing.Report();
Console.WriteLine("[server] beendet");
return 0;

static double? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length
        && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
        ? v : null;
}

/// <summary>Wall time per tick and allocations, reported as a running summary.</summary>
sealed class TickTiming
{
    long ticks;
    double sumMicros, maxMicros;
    long allocated;
    readonly double[] samples;

    public TickTiming(int capacity)
    {
        samples = new double[capacity];
    }

    public void Tick(SimulationSystem simulation)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        simulation.Tick();
        double micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        allocated += GC.GetAllocatedBytesForCurrentThread() - before;

        if (ticks < samples.Length)
            samples[ticks] = micros;
        ticks++;
        sumMicros += micros;
        if (micros > maxMicros)
            maxMicros = micros;
    }

    public void Report()
    {
        if (ticks == 0)
            return;
        Console.WriteLine($"[server] Tick {SimulationSystem.CurrentTick}: Ø {sumMicros / ticks:F1} µs, " +
                          $"max {maxMicros:F1} µs, {(double)allocated / ticks:F1} B je Tick");
    }

    /// <summary>Median and 99th percentile of the recorded ticks.</summary>
    public void ReportSpread()
    {
        int n = (int)Math.Min(ticks, samples.Length);
        if (n == 0)
            return;
        double[] sorted = samples[..n];
        Array.Sort(sorted);
        Console.WriteLine($"[server] Median {sorted[n / 2]:F1} µs, 99 % {sorted[Math.Min(n - 1, n * 99 / 100)]:F1} µs");
    }
}
