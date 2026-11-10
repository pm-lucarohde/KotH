using System;
using System.Collections.Generic;
using UnityEngine;

namespace KotH.MapKit
{
    /// <summary>Optional bot tuning for this map, by name: ThinkTicks, AimError, BlasterAimError,
    /// TurnRate, EngageRange, BlasterRange, Randomness, Persistence, StartWait, CellSize,
    /// RouteVariants, RouteDetour, JumpUp, DoubleJumpUp, GapMax.</summary>
    [AddComponentMenu("KotH map/Map bots")]
    [DisallowMultipleComponent]
    public sealed class MapBots : MonoBehaviour
    {
        public static readonly string[] Names =
        {
            "ThinkTicks", "AimError", "BlasterAimError", "TurnRate", "EngageRange", "BlasterRange", "Randomness",
            "Persistence", "StartWait", "CellSize", "RouteVariants", "RouteDetour", "JumpUp", "DoubleJumpUp", "GapMax",
        };

        [Serializable]
        public struct Setting
        {
            public string name;
            public double value;
        }

        public List<Setting> settings = new List<Setting>();
    }
}
