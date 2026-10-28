using System;
using System.Buffers;
using System.Collections.Generic;
using KotH.Probes.Harness;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>The state as bytes: a snapshot sent into a second world carries on bit for bit
    /// through a round reset; a rollback across a join or leave lands on the unrolled run's bytes;
    /// a fresh body is the same everywhere; damaged bytes are refused or show.</summary>
    public sealed class NetStateProbe : Probe
    {
        protected override string Tag => "netstate";

        // Stride metres: two players by the start, one alone on the hill.
        static readonly Vector3[] Spots =
        {
            new Vector3(-2f, 0.9f, 0f), new Vector3(0f, 5.95f, -16f), new Vector3(2f, 0.9f, 0f),
            new Vector3(4f, 0.9f, 0f), new Vector3(-4f, 0.9f, 0f),
        };

        const int DummyId = 1;

        sealed class Side
        {
            public HeadlessWorld World;
            public SimulationSystem Sim;
            public readonly Dictionary<int, SimulationBody> ById = new Dictionary<int, SimulationBody>();
        }

        readonly ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
        readonly WorldSnapshot scratch = new WorldSnapshot();
        int failures;

        public override int Run(HeadlessWorld host, int maxTicks)
        {
            Side a = Build(host, 3);
            using var second = new HeadlessWorld();
            Side b = Build(second, 3);
            Log($"[netstate] BodyState {StateCodec.BodyStateSize} bytes, {a.Sim.Bodies.Count} bodies per world");
            Check(StateCodec.BodyStateSize == 307, "BodyState without padding (307 bytes)");

            Transfer(a, b);
            JoinRollback(a);
            LeaveRollback(a);
            FreshSpawn(a, b);
            Damaged(a, b);

            Log(failures == 0 ? "[netstate] PASSED" : $"[netstate] {failures} errors");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Arena, the round, a dummy and players 2..players+1, all by NetId.</summary>
        Side Build(HeadlessWorld world, int players)
        {
            var side = new Side { World = world, Sim = world.Simulation };
            world.LoadArena(Maps.Test);
            HillMatch match = world.StartMatch();
            // A short round, so a reset to the spawn falls inside the run.
            match.WinTicks = 2 * HillMatch.TicksPerPoint;
            side.Sim.Deactivate();

            Entity marker = world.Find("DummySpawn");
            Entity dummy = world.SpawnBody(HeadlessWorld.DummyBodyUrl, marker.Transform.Position, marker.Transform.Rotation);
            Add(side, dummy.Get<SimulationBody>(), DummyId);
            for (int i = 0; i < players; i++)
                Spawn(side, 2 + i, Spots[i]);
            return side;
        }

        SimulationBody Spawn(Side side, int id, Vector3 at)
        {
            var body = new ScriptedBody();
            Entity e = side.World.SpawnBody(HeadlessWorld.PlayerBodyUrl, at, Quaternion.Identity,
                                            body, new DamagePercent());
            e.Name = "P" + id;
            Add(side, body, id);
            return body;
        }

        static void Add(Side side, SimulationBody body, int id)
        {
            body.NetId = id;
            side.ById[id] = body;
        }

        /// <summary>
        /// Seeded inputs per body and tick, the same in every world. The hill
        /// player stands still, so the short round ends and resets.
        /// </summary>
        static MoveInput InputFor(int id, int tick)
        {
            if (id == DummyId || id == 3)
                return new MoveInput { Yaw = id == DummyId ? -90f : 0f };

            uint h = (uint)(id * 73856093) ^ (uint)(tick / 20 * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return new MoveInput
            {
                Move = new Vector2(((h & 3) - 1.5f) / 1.5f, (((h >> 2) & 3) - 1f) / 2f),
                Yaw = (h >> 4) % 360,
                Pitch = ((h >> 13) % 60) - 30f,
                Sprint = ((h >> 20) & 1) != 0,
                JumpPresses = (byte)(tick % 37 == id ? 1 : 0),
                JumpHeld = tick % 37 == id,
                AttackPresses = (byte)(tick % 23 == id ? 1 : 0),
            };
        }

        readonly List<MoveInput> row = new List<MoveInput>();

        void Step(Side side)
        {
            row.Clear();
            int tick = side.Sim.CurrentTick;
            foreach (SimulationBody body in side.Sim.Bodies)
                row.Add(InputFor(body.NetId, tick));
            side.Sim.Step(row, SimulationSystem.TickSeconds);
        }

        void Steps(Side side, int n)
        {
            for (int i = 0; i < n; i++)
                Step(side);
        }

        byte[] Encode(Side side)
        {
            side.Sim.Capture(scratch);
            buffer.Clear();
            StateCodec.Write(scratch, buffer);
            return buffer.WrittenSpan.ToArray();
        }

        bool Decode(Side side, byte[] data)
        {
            if (!StateCodec.Read(data, id => side.ById.TryGetValue(id, out SimulationBody b) ? b : null, scratch))
                return false;
            side.Sim.Restore(scratch);
            return true;
        }

        static ulong Hash(byte[] data) => StateCodec.Hash(data);

        /// <summary>A's state into B mid warm-up; both run on, through the win and the reset.</summary>
        void Transfer(Side a, Side b)
        {
            Steps(a, 150);
            Steps(b, 37);
            byte[] sent = Encode(a);
            Check(Decode(b, sent), "state read into the second world");
            Check(Hash(Encode(b)) == Hash(sent), $"second world bit-identical at once ({sent.Length} bytes)");

            int first = -1;
            bool reset = false;
            for (int t = 0; t < 600; t++)
            {
                Step(a);
                Step(b);
                reset |= a.Sim.Match.State.RoundTicks == 0 && a.Sim.CurrentTick > 300;
                if (first < 0 && Hash(Encode(a)) != Hash(Encode(b)))
                    first = t;
            }
            Check(first < 0, first < 0 ? "600 ticks on bit-identical" : $"differs from tick {first} on");
            Check(reset, "a round ended and reset everyone to the spawn");
        }

        /// <summary>A body joins after a snapshot; a rollback parks it, the replay brings it back.</summary>
        void JoinRollback(Side a)
        {
            var before = new WorldSnapshot();
            a.Sim.Capture(before);
            Steps(a, 5);
            SimulationBody joined = Spawn(a, 5, Spots[3]);
            BodyState joinState = joined.CaptureState();
            Steps(a, 15);
            byte[] straight = Encode(a);

            a.Sim.Restore(before);
            Check(joined.IsParked && !Contains(a.Sim.Bodies, joined) && joined.Motor.Hull.Entity.Transform.Position.Y < -1000f,
                  "rollback before the join parks the body and its hull");

            Steps(a, 5);
            a.Sim.Rejoin(joined, joinState);
            Steps(a, 15);
            Check(Hash(Encode(a)) == Hash(straight), "replay with join ends bit-identical");
        }

        /// <summary>A body leaves after a snapshot; a rollback brings it back at its old place.</summary>
        void LeaveRollback(Side a)
        {
            var before = new WorldSnapshot();
            a.Sim.Capture(before);
            string order = Order(a.Sim.Bodies);
            SimulationBody leaving = a.ById[3];
            Steps(a, 5);
            a.Sim.Leave(leaving);
            Steps(a, 15);
            byte[] straight = Encode(a);

            a.Sim.Restore(before);
            Check(!leaving.IsParked && Order(a.Sim.Bodies) == order,
                  $"rollback before leaving brings him back to his place ({order})");

            Steps(a, 5);
            a.Sim.Leave(leaving);
            Steps(a, 15);
            Check(Hash(Encode(a)) == Hash(straight), "replay with leaving ends bit-identical");
        }

        /// <summary>The same prefab at the same spot is the same state in any world.</summary>
        void FreshSpawn(Side a, Side b)
        {
            SimulationBody x = Spawn(a, 6, Spots[4]);
            SimulationBody y = Spawn(b, 6, Spots[4]);
            Check(Same(x.CaptureState(), y.CaptureState()), "fresh body bit-identical in both worlds");
        }

        /// <summary>Damaged bytes are refused or change the state.</summary>
        void Damaged(Side a, Side b)
        {
            byte[] good = Encode(a);
            byte[] bad = (byte[])good.Clone();
            bad[0] ^= 1;
            Check(!StateCodec.Read(bad, id => a.ById.GetValueOrDefault(id), scratch), "wrong header rejected");

            bad = (byte[])good.Clone();
            bad[StateCodec.HeadBytes + StateCodec.MatchBytes] = 99;     // first body's NetId
            Check(!StateCodec.Read(bad, id => a.ById.GetValueOrDefault(id), scratch), "unknown NetId rejected");

            bad = (byte[])good.Clone();
            bad[StateCodec.HeadBytes + StateCodec.MatchBytes + 34] ^= 0x40;   // a byte of the first body's velocity
            Check(StateCodec.Read(bad, id => a.ById.GetValueOrDefault(id), scratch), "flipped byte read");
            a.Sim.Restore(scratch);
            Check(Hash(Encode(a)) != Hash(good), "flipped byte changes the state");
        }

        static bool Contains(IReadOnlyList<SimulationBody> list, SimulationBody body)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == body)
                    return true;
            return false;
        }

        static string Order(IReadOnlyList<SimulationBody> list)
        {
            var ids = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                ids[i] = list[i].NetId.ToString();
            return string.Join(",", ids);
        }

        static bool Same(BodyState x, BodyState y)
        {
            Span<byte> p = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref x, 1));
            Span<byte> q = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref y, 1));
            return p.SequenceEqual(q);
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[netstate] {(ok ? "ok    " : "ERROR ")} {what}");
        }
    }
}
