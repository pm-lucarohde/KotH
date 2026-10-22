using System;
using System.Collections.Generic;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Mathematics;
using KotH.Shared.Net;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Shared.Bots
{
    /// <summary>States no rollback can change any more, by the tick they start.</summary>
    public interface IFinalStates
    {
        bool TryFinalState(int tick, out WorldSnapshot state);
    }

    /// <summary>Inputs known before they are confirmed: the host's bots.</summary>
    public interface IBotInputs
    {
        bool IsBot(int netId);
        bool TryInput(int netId, int tick, out MoveInput input);
    }

    /// <summary>
    /// Runs every bot of a world. Each input is worked out exactly once, in
    /// tick order, from the final state ReactionTicks before, and kept: the
    /// host confirms it, the host's own peer predicts with it, and asking
    /// again, even after a rollback, gives the same bits.
    /// </summary>
    public sealed class BotDirector : IBotInputs
    {
        const int Memo = 256;

        sealed class Bot
        {
            public BotBrain Brain;
            public int Last = -1;
            public readonly MoveInput[] Inputs = new MoveInput[Memo];
        }

        readonly Dictionary<int, Bot> bots = new Dictionary<int, Bot>();
        readonly IFinalStates states;
        readonly BotSettings settings;
        readonly HillMatch match;
        readonly PlayerMotor motor;
        readonly MeleeAttack fists;
        readonly int seed;

        public BotDirector(BotNavigation navigation, BotSettings settings, HillMatch match,
                           PlayerMotor motor, MeleeAttack fists, IFinalStates states, int seed)
        {
            Navigation = navigation;
            this.settings = settings;
            this.match = match;
            this.motor = motor;
            this.fists = fists;
            this.states = states;
            this.seed = seed;
        }

        /// <summary>
        /// The climbing grid and the bodies' settings out of the arena and the
        /// player prefab, the same way for the game, the server and probes.
        /// </summary>
        public static BotDirector Create(WorldBuilder builder, SimulationSystem simulation, IFinalStates states, int seed)
        {
            BotSettings settings = builder.Bots ?? new BotSettings();
            PlayerMotor motor = builder.Template<PlayerMotor>(WorldBuilder.PlayerBodyUrl);
            MeleeAttack fists = builder.Template<MeleeAttack>(WorldBuilder.PlayerBodyUrl) ?? new MeleeAttack();
            HillMatch zone = simulation.Match ?? (builder.Zone != null ? new HillMatch(builder.Zone) : null);
            BotNavigation nav = BotNavigation.Build(builder.Arena, motor, settings, zone, seed, SpawnMiddle(builder));
            return new BotDirector(nav, settings, simulation.Match, motor, fists, states, seed);
        }

        // Where the players start, on average: the route variants fan out from there.
        static Vector3 SpawnMiddle(WorldBuilder builder)
        {
            Vector3 sum = Vector3.Zero;
            int n = 0;
            for (int slot = 0; slot < NetWorld.BotSlots.Length; slot++)
            {
                Entity marker = NetWorld.SlotMarker(builder, slot);
                if (marker == null)
                    continue;
                sum = UMath.Add(sum, marker.Transform.Position);
                n++;
            }
            return n > 0 ? UMath.Mul(sum, 1f / n) : Vector3.Zero;
        }

        public BotNavigation Navigation { get; }
        public BotSettings Settings => settings;
        public int Seed => seed;
        public int Count => bots.Count;

        public void Add(int netId)
        {
            bots[netId] = new Bot
            {
                Brain = new BotBrain(netId, BotRandom.Seed(seed, netId), bots.Count + seed, Navigation, settings, match, motor, fists),
            };
        }

        public bool IsBot(int netId) => bots.ContainsKey(netId);

        public BotBrain BrainOf(int netId) => bots.TryGetValue(netId, out Bot b) ? b.Brain : null;

        /// <summary>
        /// The bot's input for a tick; false while the state it needs is not
        /// final yet. Starts where it is first asked: a bot is asked from the
        /// tick it joins, and sees itself only ReactionTicks later.
        /// </summary>
        public bool TryInput(int netId, int tick, out MoveInput input)
        {
            input = default;
            if (!bots.TryGetValue(netId, out Bot bot))
                return false;
            if (bot.Last < 0)
                bot.Last = tick - 1;
            if (tick <= bot.Last)
            {
                if (tick <= bot.Last - Memo)
                    return false;
                input = bot.Inputs[tick % Memo];
                return true;
            }

            int reaction = Math.Max(3, settings.ReactionTicks);
            for (int u = bot.Last + 1; u <= tick; u++)
            {
                WorldSnapshot view = null;
                if (u - reaction >= 0 && !states.TryFinalState(u - reaction, out view))
                    return false;
                bot.Inputs[u % Memo] = Net.NetProtocol.Sanitize(bot.Brain.Step(u, view));
                bot.Last = u;
            }
            input = bot.Inputs[tick % Memo];
            return true;
        }
    }

    /// <summary>
    /// A world without rollback, offline or headless: every tick is final
    /// as soon as it starts. Records the start of each tick it is told of.
    /// </summary>
    public sealed class LocalFinalStates : IFinalStates
    {
        const int Ring = 64;

        readonly SimulationSystem simulation;
        readonly WorldSnapshot[] snaps = new WorldSnapshot[Ring];
        readonly int[] ticks = new int[Ring];
        int recorded = -1;

        public LocalFinalStates(SimulationSystem simulation)
        {
            this.simulation = simulation;
            for (int i = 0; i < Ring; i++)
            {
                snaps[i] = new WorldSnapshot();
                ticks[i] = -1;
            }
        }

        /// <summary>Records the tick about to run, once, before any input of it is read; returns it.</summary>
        public int Note()
        {
            int tick = simulation.CurrentTick;
            if (tick <= recorded)
                return tick;
            simulation.Capture(snaps[tick % Ring]);
            ticks[tick % Ring] = tick;
            recorded = tick;
            return tick;
        }

        public bool TryFinalState(int tick, out WorldSnapshot state)
        {
            state = snaps[tick % Ring];
            return tick >= 0 && ticks[tick % Ring] == tick;
        }
    }

    /// <summary>
    /// A bot's body where the world has no netcode: offline and headless.
    /// Notes the tick for the director, then asks it, like a player asks
    /// the keyboard.
    /// </summary>
    [Stride.Core.DataContract]
    public sealed class BotBody : SimulationBody
    {
        readonly BotDirector director;
        readonly LocalFinalStates states;

        /// <summary>For the serializer only; such a body stands still.</summary>
        public BotBody() { }

        public BotBody(BotDirector director, LocalFinalStates states, int netId)
        {
            this.director = director;
            this.states = states;
            NetId = netId;
            director.Add(netId);
        }

        public override MoveInput ReadInput()
        {
            MoveInput input = default;
            director?.TryInput(NetId, states.Note(), out input);
            return input;
        }
    }
}
