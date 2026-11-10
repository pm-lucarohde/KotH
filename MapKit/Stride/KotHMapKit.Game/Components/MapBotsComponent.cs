using System.Collections.Generic;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.MapKit
{
    /// <summary>Optional bot tuning for this map, by name: ThinkTicks, AimError, BlasterAimError,
    /// TurnRate, EngageRange, BlasterRange, Randomness, Persistence, StartWait, CellSize,
    /// RouteVariants, RouteDetour, JumpUp, DoubleJumpUp, GapMax.</summary>
    [DataContract("KotH.MapKit.MapBots")]
    [Display("Map bots")]
    [ComponentCategory("KotH map")]
    public sealed class MapBotsComponent : EntityComponent
    {
        public static readonly string[] Names =
        {
            "ThinkTicks", "AimError", "BlasterAimError", "TurnRate", "EngageRange", "BlasterRange", "Randomness",
            "Persistence", "StartWait", "CellSize", "RouteVariants", "RouteDetour", "JumpUp", "DoubleJumpUp", "GapMax",
        };

        [DataMember(10)] public Dictionary<string, double> Settings { get; set; } = new Dictionary<string, double>();
    }
}
