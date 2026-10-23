using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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

        /// <summary>The newest tick whose starting state is final; -1 before any.</summary>
        int NewestFinal { get; }
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
    /// Since a state is final that long before its bots' inputs are due,
    /// they can be worked out ahead: on a thread of their own (StartWorker)
    /// or a little each frame (Pump), from copies of the final states. What
    /// is not ready when asked is worked out then, as without either.
    /// </summary>
    public sealed class BotDirector : IBotInputs, IDisposable
    {
        const int Memo = 256;
        const int Views = 64;

        sealed class Bot
        {
            public BotBrain Brain;
            public bool Asked;           // asked once: from then on it may be worked out ahead
            public int Last = -1;        // the newest tick worked out; written under the gate
            public readonly MoveInput[] Inputs = new MoveInput[Memo];
        }

        readonly Dictionary<int, Bot> bots = new Dictionary<int, Bot>();
        readonly IFinalStates states;
        readonly BotSettings settings;
        readonly HillMatch match;
        readonly PlayerMotor motor;
        readonly MeleeAttack fists;
        readonly int seed;

        // Working ahead. One step at a time under the gate: the brains share
        // the grid's search. The views are the director's own copies, filled
        // by the game's thread only, never where a bot still needs one.
        readonly object gate = new object();
        Bot[] roster = Array.Empty<Bot>();
        readonly WorldSnapshot[] views = new WorldSnapshot[Views];
        readonly int[] viewTicks = new int[Views];
        int fed = -1;
        Thread worker;
        AutoResetEvent wake;
        volatile bool stopping;

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
            for (int i = 0; i < Views; i++)
            {
                views[i] = new WorldSnapshot();
                viewTicks[i] = -1;
            }
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

        /// <summary>Steps worked out ahead, and those only when asked; for probes.</summary>
        public int StepsAhead { get; private set; }
        public int StepsAsked { get; private set; }

        int Reaction => Math.Max(3, settings.ReactionTicks);

        public void Add(int netId)
        {
            var bot = new Bot
            {
                Brain = new BotBrain(netId, BotRandom.Seed(seed, netId), bots.Count + seed, Navigation, settings, match, motor, fists),
            };
            lock (gate)
            {
                bots[netId] = bot;
                roster = new List<Bot>(bots.Values).ToArray();
            }
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
            // Worked out ahead already: no need to wait for the gate.
            int last = Volatile.Read(ref bot.Last);
            if (bot.Asked && tick <= last)
            {
                if (tick <= last - Memo)
                    return false;
                input = bot.Inputs[tick % Memo];
                return true;
            }

            lock (gate)
            {
                if (!bot.Asked)
                {
                    bot.Asked = true;
                    bot.Last = tick - 1;
                }
                if (tick <= bot.Last)
                {
                    if (tick <= bot.Last - Memo)
                        return false;
                    input = bot.Inputs[tick % Memo];
                    return true;
                }

                int reaction = Reaction;
                for (int u = bot.Last + 1; u <= tick; u++)
                {
                    WorldSnapshot view = null;
                    if (u - reaction >= 0 && !states.TryFinalState(u - reaction, out view))
                        return false;
                    bot.Inputs[u % Memo] = Net.NetProtocol.Sanitize(bot.Brain.Step(u, view));
                    Volatile.Write(ref bot.Last, u);
                    StepsAsked++;
                }
                input = bot.Inputs[tick % Memo];
                return true;
            }
        }

        /// <summary>
        /// Once per frame on the game's thread, after its ticks: hands the
        /// newly final states on, then wakes the worker, or without one works
        /// ahead itself for up to budget milliseconds (at least one step).
        /// </summary>
        public void Pump(double budget)
        {
            if (Feed() && worker != null)
                wake.Set();
            if (worker == null)
                Advance(Stopwatch.GetTimestamp() + (long)(budget * Stopwatch.Frequency / 1000.0));
        }

        /// <summary>Works the bots out ahead on a thread of their own from now on.</summary>
        public void StartWorker()
        {
            if (worker != null)
                return;
            wake = new AutoResetEvent(false);
            worker = new Thread(() =>
            {
                while (!stopping)
                {
                    wake.WaitOne();
                    Advance(long.MaxValue);
                }
            })
            { Name = "KotH Bots", IsBackground = true };
            worker.Start();
        }

        public void Dispose()
        {
            if (worker == null)
                return;
            stopping = true;
            wake.Set();
            worker.Join();
            worker = null;
            wake.Dispose();
        }

        // Copies the states final since the last call, oldest first, as far
        // as no bot still needs the slot each one goes to; true if any came.
        bool Feed()
        {
            int newest = states.NewestFinal;
            int needed = int.MaxValue;
            foreach (Bot b in roster)
                if (b.Asked)
                    needed = Math.Min(needed, Volatile.Read(ref b.Last) + 1 - Reaction);
            bool any = false;
            for (int k = Math.Max(fed + 1, newest - Views + 1); k <= newest && k - Views < needed; k++)
            {
                int slot = k % Views;
                Volatile.Write(ref viewTicks[slot], -1);
                if (states.TryFinalState(k, out WorldSnapshot state))
                {
                    views[slot].CopyFrom(state);
                    Volatile.Write(ref viewTicks[slot], k);
                }
                Volatile.Write(ref fed, k);
                any = true;
            }
            return any;
        }

        // Steps the bot furthest behind whose view is in hand, one tick at a
        // time, until none can go on or the deadline (a Stopwatch timestamp) passes.
        void Advance(long deadline)
        {
            int reaction = Reaction;
            while (!stopping)
            {
                lock (gate)
                {
                    int limit = Volatile.Read(ref fed) + reaction;
                    Bot next = null;
                    WorldSnapshot nextView = null;
                    foreach (Bot b in roster)
                    {
                        if (!b.Asked || b.Last >= limit || next != null && b.Last >= next.Last)
                            continue;
                        int k = b.Last + 1 - reaction;
                        if (k >= 0 && Volatile.Read(ref viewTicks[k % Views]) != k)
                            continue;
                        next = b;
                        nextView = k >= 0 ? views[k % Views] : null;
                    }
                    if (next == null)
                        return;
                    int u = next.Last + 1;
                    next.Inputs[u % Memo] = Net.NetProtocol.Sanitize(next.Brain.Step(u, nextView));
                    Volatile.Write(ref next.Last, u);
                    StepsAhead++;
                }
                if (Stopwatch.GetTimestamp() >= deadline)
                    return;
            }
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

        public int NewestFinal => recorded;
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
