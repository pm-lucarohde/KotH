using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KotH.Shared.Match;
using KotH.Shared.Simulation;

namespace KotH.Shared.Net
{
    /// <summary>A snapshot as bytes and back, bit for bit: what a joining peer and a
    /// desync repair receive. Body states go over as their packed memory, which every
    /// peer reads the same way because all run the same build. Bodies are named by NetId,
    /// in tick order; the round names them too.</summary>
    public static class StateCodec
    {
        const uint Magic = 0x4B6F5448; // "KoTH"
        const int Version = 3;

        /// <summary>Bytes before the round: magic, version, tick, body count.</summary>
        public const int HeadBytes = 16;

        // RoundTicks, Phase, PhaseTicks, Inside, Winner, Holder, MinPlayers, Seed.
        public const int MatchBytes = 4 + 1 + 4 + 4 + 4 + 4 + 1 + 4;

        public static int BodyStateSize => Unsafe.SizeOf<BodyState>();

        /// <summary>Appends the snapshot, each body's spawn state included.</summary>
        public static void Write(WorldSnapshot snapshot, IBufferWriter<byte> into)
        {
            int count = snapshot.Count;
            Span<byte> head = into.GetSpan(HeadBytes + MatchBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(head[4..], Version);
            BinaryPrimitives.WriteInt32LittleEndian(head[8..], snapshot.Tick);
            BinaryPrimitives.WriteInt32LittleEndian(head[12..], count);
            WriteMatch(head[HeadBytes..], snapshot.Match);
            into.Advance(HeadBytes + MatchBytes);

            for (int i = 0; i < count; i++)
            {
                SimulationBody body = snapshot.BodyAt(i);
                BodyState state = snapshot.StateAt(i);
                BodyState spawn = body.SpawnState;

                Span<byte> rec = into.GetSpan(5 + 2 * BodyStateSize);
                BinaryPrimitives.WriteInt32LittleEndian(rec, body.NetId);
                rec[4] = body.HasSpawn ? (byte)1 : (byte)0;
                Bytes(ref state).CopyTo(rec[5..]);
                Bytes(ref spawn).CopyTo(rec[(5 + BodyStateSize)..]);
                into.Advance(5 + 2 * BodyStateSize);
            }
        }

        /// <summary>Fills a snapshot from bytes, finding each body by its NetId, and
        /// hands the spawn states over. False for bytes of another build or a body this
        /// peer does not have. SimulationSystem.Restore applies it.</summary>
        public static bool Read(ReadOnlySpan<byte> data, Func<int, SimulationBody> bodyOf, WorldSnapshot into)
        {
            into.Clear();
            if (data.Length < HeadBytes + MatchBytes ||
                BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic ||
                BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != Version)
                return false;

            into.Tick = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
            ReadOnlySpan<byte> matchBytes = data.Slice(HeadBytes, MatchBytes);
            data = data[(HeadBytes + MatchBytes)..];

            int size = 5 + 2 * BodyStateSize;
            if (count < 0 || data.Length != count * size)
                return false;

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> rec = data.Slice(i * size, size);
                SimulationBody body = bodyOf(BinaryPrimitives.ReadInt32LittleEndian(rec));
                if (body == null)
                    return false;

                BodyState state = MemoryMarshal.Read<BodyState>(rec[5..]);
                if (rec[4] != 0)
                    body.SetSpawn(MemoryMarshal.Read<BodyState>(rec[(5 + BodyStateSize)..]));
                into.Add(body, state);
            }

            into.Match = ReadMatch(matchBytes, bodyOf);
            return true;
        }

        /// <summary>FNV-1a over bytes: the checksum peers compare.</summary>
        public static ulong Hash(ReadOnlySpan<byte> data)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < data.Length; i++)
                h = (h ^ data[i]) * 1099511628211UL;
            return h;
        }

        static void WriteMatch(Span<byte> to, in MatchState m)
        {
            BinaryPrimitives.WriteInt32LittleEndian(to, m.RoundTicks);
            to[4] = (byte)m.Phase;
            BinaryPrimitives.WriteInt32LittleEndian(to[5..], m.PhaseTicks);
            BinaryPrimitives.WriteInt32LittleEndian(to[9..], m.Inside);
            BinaryPrimitives.WriteInt32LittleEndian(to[13..], m.Winner?.NetId ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(to[17..], m.Holder?.NetId ?? 0);
            to[21] = (byte)m.MinPlayers;
            BinaryPrimitives.WriteUInt32LittleEndian(to[22..], m.Seed);
        }

        static MatchState ReadMatch(ReadOnlySpan<byte> from, Func<int, SimulationBody> bodyOf)
        {
            int winner = BinaryPrimitives.ReadInt32LittleEndian(from[13..]);
            int holder = BinaryPrimitives.ReadInt32LittleEndian(from[17..]);
            return new MatchState
            {
                RoundTicks = BinaryPrimitives.ReadInt32LittleEndian(from),
                Phase = (MatchPhase)from[4],
                PhaseTicks = BinaryPrimitives.ReadInt32LittleEndian(from[5..]),
                Inside = BinaryPrimitives.ReadInt32LittleEndian(from[9..]),
                Winner = winner != 0 ? bodyOf(winner) : null,
                Holder = holder != 0 ? bodyOf(holder) : null,
                MinPlayers = from[21],
                Seed = BinaryPrimitives.ReadUInt32LittleEndian(from[22..]),
            };
        }

        static Span<byte> Bytes(ref BodyState state)
            => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref state, 1));
    }
}
