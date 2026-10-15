using System;
using System.Globalization;
using System.IO;
using KotH.Probes.Harness;
using KotH.Probes.Probes;
using KotH.Shared.Hosting;

// Unity logged with the system culture, de-DE here; the reference logs
// carry decimal commas, so these have to as well.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("de-DE");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

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
    "sprint" => new SprintProbe(),
    _ => null,
};

if (probe == null)
{
    Console.Error.WriteLine("Aufruf: KotH.Probes <worldquery|rotation|slopejump|crest|impact|jumpheight|sprint>");
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
