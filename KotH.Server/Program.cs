using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using KotH.Shared.Hosting;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Engine;

// The headless host: the arena, the training dummy and the 40 Hz tick, no
// window and no GPU. Players and netcode come later; the loop is the one a
// peer acting as host will run.
//
//   KotH.Server                 runs in real time until Ctrl+C
//   KotH.Server --seconds N     runs in real time for N seconds
//   KotH.Server --ticks N       runs N ticks as fast as it can, then prints
//                               the timing and every body's exact state

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("de-DE");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

double? seconds = Option(args, "--seconds");
double? fastTicks = Option(args, "--ticks");

using var world = new HeadlessWorld();
world.LoadArena();

Entity spawn = world.Find("DummySpawn");
Entity dummy = world.SpawnBody(HeadlessWorld.DummyBodyUrl, spawn.Transform.Position, spawn.Transform.Rotation);
dummy.Name = "Dummy";

Console.WriteLine($"[server] Arena geladen, {SimulationSystem.Bodies.Count} Körper, {1.0 / TickClock.TickSeconds:F0} Hz");

var timing = new TickTiming();

if (fastTicks.HasValue)
{
    for (int i = 0; i < (int)fastTicks.Value; i++)
        timing.Tick(world.Simulation);

    timing.Report();
    foreach (SimulationBody body in SimulationSystem.Bodies)
    {
        MotorState s = body.Entity.Get<PlayerMotor>().CaptureState();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "[server] {0}: Position {1:R} {2:R} {3:R}, Tempo {4:R} {5:R} {6:R}",
            body.Entity.Name, s.Position.X, s.Position.Y, s.Position.Z, s.Velocity.X, s.Velocity.Y, s.Velocity.Z));
    }
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

    public void Tick(SimulationSystem simulation)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        simulation.Tick();
        double micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        allocated += GC.GetAllocatedBytesForCurrentThread() - before;

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
}
