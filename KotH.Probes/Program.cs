using System;
using System.Globalization;
using System.IO;
using KotH.Probes.Harness;
using KotH.Probes.Probes;
using KotH.Shared.Hosting;

// Decimal points everywhere; the Unity reference logs were converted to match.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string name = args.Length > 0 ? args[0] : "";
string repo = FindRepo();
string dump = Path.Combine(repo, "Reference", "unity", "dump");

Probe probe = name switch
{
    "worldquery" => new WorldQueryProbe(dump),
    "rotation" => new RotationProbe(dump),
    "slopejump" => new SlopeJumpProbe(),
    "crest" => new CrestProbe(),
    "impact" => new ImpactProbe(),
    "jumpheight" => new JumpHeightProbe(),
    "slide" => new SlideProbe(),
    "sprint" => new SprintProbe(),
    "melee" => new MeleeProbe(),
    "blaster" => new BlasterProbe(),
    "step" => new StepProbe(),
    "determinism" => new DeterminismProbe { PrintHashes = Array.IndexOf(args, "hashes") > 0 },
    "clock" => new ClockProbe(dump),
    "stairrun" => new StairRunProbe(),
    "rampfoot" => new RampFootProbe(),
    "crouchedge" => new CrouchEdgeProbe(),
    "knockback" => new KnockbackProbe(),
    "thirdperson" => new ThirdPersonProbe(),
    "wallknock" => new WallKnockProbe { Trace = Array.IndexOf(args, "trace") > 0 },
    "capture" => new CaptureProbe(),
    "netstate" => new NetStateProbe(),
    "netplay" => new NetPlayProbe(),
    "bots" => new BotProbe
    {
        Stats = args.Length > 2 && args[1] == "stats" ? int.Parse(args[2]) : 0,
        Climb = args.Length > 1 && args[1] == "climb" ? (args.Length > 2 ? int.Parse(args[2]) : 1) : 0,
        Lab = args.Length > 1 && args[1] == "jumplab" ? (args.Length > 2 ? int.Parse(args[2]) : 200) : 0,
    },
    _ => null,
};

if (probe == null)
{
    Console.Error.WriteLine("Usage: KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|slide|sprint|melee|blaster|step|determinism|clock|stairrun|rampfoot|crouchedge|knockback|thirdperson|wallknock|capture|netstate|netplay|bots>");
    return 64;
}

using var world = new HeadlessWorld();

// 600 s of simulated time, the watchdog the Unity runners meant to have.
return probe.Run(world, 24000);

static string FindRepo()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "KotH.slnx")))
            return d.FullName;
    return Directory.GetCurrentDirectory();
}
