using System.Collections.Generic;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Shared.Net
{
    /// <summary>
    /// A player steered from the network. Its input never comes from here:
    /// the session steps the world itself with what the host confirmed or
    /// what it predicts, so ReadInput is only ever asked offline.
    /// </summary>
    [DataContract]
    public sealed class NetBody : SimulationBody
    {
        public override MoveInput ReadInput() => default;
    }

    /// <summary>
    /// Where a session gets its bodies from. The client also draws them, the
    /// server and the probes do not; the session does not care.
    /// </summary>
    public interface INetWorld
    {
        SimulationSystem Simulation { get; }

        /// <summary>A fresh player at its slot, already in the tick; the session parks it.</summary>
        SimulationBody SpawnPlayer(int netId, int slot, string name);

        /// <summary>Removes a player for good, once no rollback can reach back to it.</summary>
        void Despawn(SimulationBody body);
    }

    /// <summary>
    /// The parts of a networked world every peer builds the same way, in the
    /// same order: the dummies get NetIds 1 to 3 before any player exists, so
    /// the hulls sit in the same query order everywhere.
    /// </summary>
    public static class NetWorld
    {
        public const int FirstPlayerId = 4;
        public const int MaxPlayers = 12;

        static readonly float[] DummyPercents = { 0f, 50f, 100f };

        /// <summary>
        /// The three dummies beside the dummy marker, 3 m apart across its
        /// facing, charged to 0, 50 and 100 percent; NetIds 1, 2, 3.
        /// </summary>
        public static List<Entity> SpawnDummies(WorldBuilder builder)
        {
            Entity marker = builder.Find("DummySpawn");
            Vector3 side = UMath.Mul(UMath.Rotate(marker.Transform.Rotation, UMath.Right), 3f);
            Vector3[] offsets = { Vector3.Zero, UMath.Neg(side), side };

            var dummies = new List<Entity>();
            for (int i = 0; i < offsets.Length; i++)
            {
                Entity body = builder.SpawnBody(WorldBuilder.DummyBodyUrl,
                    UMath.Add(marker.Transform.Position, offsets[i]), marker.Transform.Rotation);
                body.Name = $"Dummy {DummyPercents[i]:F0} %";
                body.Get<DamagePercent>().Restore(DummyPercents[i]);
                body.Get<SimulationBody>().NetId = 1 + i;
                dummies.Add(body);
            }
            return dummies;
        }

        /// <summary>
        /// Where a player slot starts and faces: the markers PlayerSpawn0 to
        /// PlayerSpawn11 in the arena, two rows of six behind the offline spawn.
        /// </summary>
        /// <summary>
        /// The slots bots take, in order: wide apart first, out to both ends
        /// of the front row, then the middle behind, and so on.
        /// </summary>
        public static readonly int[] BotSlots = { 0, 5, 8, 11, 2, 6, 3, 9, 1, 10, 4, 7 };

        public static Entity SlotMarker(WorldBuilder builder, int slot)
            => builder.Find("PlayerSpawn" + slot);
    }

    /// <summary>The world of a host or peer without graphics: server and probes.</summary>
    public sealed class HeadlessNetWorld : INetWorld
    {
        readonly HeadlessWorld world;
        readonly WorldBuilder builder;

        public HeadlessNetWorld(HeadlessWorld world)
        {
            this.world = world;
            builder = world.Builder;
        }

        public SimulationSystem Simulation => world.Simulation;

        public SimulationBody SpawnPlayer(int netId, int slot, string name)
        {
            var body = new NetBody { NetId = netId };
            Entity marker = NetWorld.SlotMarker(builder, slot);
            Entity e = builder.SpawnBody(WorldBuilder.PlayerBodyUrl, marker.Transform.Position,
                                         marker.Transform.Rotation, body, new DamagePercent());
            e.Name = name;
            return body;
        }

        public void Despawn(SimulationBody body)
        {
            world.Simulation.Unregister(body);
            builder.Scene.Entities.Remove(body.Entity);
        }
    }
}
