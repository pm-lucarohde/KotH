using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;

namespace KotH.Shared.Net
{
    /// <summary>The first byte of every message.</summary>
    public enum MsgType : byte
    {
        Hello = 1,      // peer -> host: build, name
        Welcome = 2,    // host -> peer: NetId, map, state, roster
        Reject = 3,     // host -> peer: why not
        Inputs = 4,     // peer -> host: own inputs not yet acknowledged
        Confirmed = 5,  // host -> peer: final inputs and events per tick
        Checksum = 6,   // peer -> host: hash of a confirmed state
        Repair = 7,     // host -> peer: the full state after a desync
        Ask = 8,        // peer -> host: build; which map, before joining
        Map = 9,        // host -> peer: the map, to load before the Hello
    }

    public enum RejectReason : byte { Version = 1, Full = 2, Closed = 3 }

    /// <summary>A join or a leave, part of the confirmed tick it happens in.</summary>
    public struct NetEvent
    {
        public bool Join;
        public int NetId;
        public int Slot;
        public string Name;
    }

    /// <summary>
    /// Wire formats shared by host and peers. Floats go over as their bits:
    /// every peer has to step with exactly the input the host confirmed.
    /// </summary>
    public static class NetProtocol
    {
        /// <summary>Which build this is. Peers only play with an identical one: the
        /// simulation module's id plus the instruction set it runs with, since float
        /// results depend on both; then the shared assets (AddAssets).</summary>
        public static readonly byte[] BuildId = MakeBuildId();

        const string SharedAssets = "/KotH.Shared/";
        static bool assetsAdded;

        static byte[] MakeBuildId()
        {
            var id = new byte[28];
            typeof(SimulationSystem).Module.ModuleVersionId.TryWriteBytes(id);
            id[16] = (byte)Vector<float>.Count;
            id[17] = (byte)((Avx.IsSupported ? 1 : 0) | (Avx2.IsSupported ? 2 : 0) | (Fma.IsSupported ? 4 : 0));
            id[18] = (byte)(Sse42.IsSupported ? 1 : 0);
            return id;
        }

        public static string BuildText => Convert.ToHexString(BuildId);

        /// <summary>Adds the shared assets to the build id, once: map collisions, spawns
        /// and bodies live in the bundle, not in the module, and a peer with other ones
        /// would desync. Stride names each object by the hash of its bytes, so their
        /// content ids, sorted by URL, are the same for client and server.</summary>
        public static void AddAssets(ContentManager content)
        {
            if (assetsAdded || content.FileProvider is not DatabaseFileProvider provider)
                return;
            var shared = new List<KeyValuePair<string, ObjectId>>();
            foreach (KeyValuePair<string, ObjectId> entry in provider.ContentIndexMap.GetMergedIdMap())
                if (entry.Key.StartsWith(SharedAssets, StringComparison.Ordinal))
                    shared.Add(entry);
            shared.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (KeyValuePair<string, ObjectId> entry in shared)
                hash.AppendData(Encoding.UTF8.GetBytes($"{entry.Key} {entry.Value}\n"));
            hash.GetHashAndReset().AsSpan(0, 8).CopyTo(BuildId.AsSpan(20));
            assetsAdded = true;
        }

        // Flags of a packed input.
        const byte Sprint = 1, Crouch = 2, CrouchPressed = 4, JumpHeld = 8, GridMove = 16, Blaster = 32;

        /// <summary>
        /// One input in 12 bytes when the move is on the keyboard grid
        /// (each axis -1, 0 or 1), else 19; yaw and pitch always exact.
        /// </summary>
        public static void WriteInput(ref Writer w, in MoveInput m)
        {
            bool grid = OnGrid(m.Move.X) && OnGrid(m.Move.Y);
            byte flags = (byte)((m.Sprint ? Sprint : 0) | (m.Crouch ? Crouch : 0) |
                                (m.CrouchPressed ? CrouchPressed : 0) | (m.JumpHeld ? JumpHeld : 0) |
                                (grid ? GridMove : 0) | (m.Blaster ? Blaster : 0));
            w.Byte(flags);
            w.Byte(m.JumpPresses);
            w.Byte(m.AttackPresses);
            if (grid)
                w.Byte((byte)((int)(m.Move.X + 1f) * 3 + (int)(m.Move.Y + 1f)));
            else
            {
                w.Float(m.Move.X);
                w.Float(m.Move.Y);
            }
            w.Float(m.Yaw);
            w.Float(m.Pitch);
        }

        public static MoveInput ReadInput(ref Reader r)
        {
            byte flags = r.Byte();
            var m = new MoveInput
            {
                Sprint = (flags & Sprint) != 0,
                Crouch = (flags & Crouch) != 0,
                CrouchPressed = (flags & CrouchPressed) != 0,
                JumpHeld = (flags & JumpHeld) != 0,
                Blaster = (flags & Blaster) != 0,
                JumpPresses = r.Byte(),
                AttackPresses = r.Byte(),
            };
            if ((flags & GridMove) != 0)
            {
                int cell = r.Byte();
                m.Move = new Stride.Core.Mathematics.Vector2(cell / 3 - 1, cell % 3 - 1);
            }
            else
                m.Move = new Stride.Core.Mathematics.Vector2(r.Float(), r.Float());
            m.Yaw = r.Float();
            m.Pitch = r.Float();
            return m;
        }

        // Exact values only, and never -0: its bits would not survive the grid.
        static bool OnGrid(float v)
            => (v == 1f || v == -1f || v == 0f) && BitConverter.SingleToInt32Bits(v) != int.MinValue;

        /// <summary>Largest datagram the protocol sends unreliably; fits every internet path.</summary>
        public const int MaxDatagram = 1200;

        /// <summary>Most presses of one key a player can make in a 25 ms tick.</summary>
        public const byte MaxPresses = 4;

        /// <summary>Furthest a yaw may have turned; no mouse gets there in a match.</summary>
        public const float MaxYaw = 1e6f;

        /// <summary>What the host lets into the game: moves within -1..1, a finite view
        /// with the pitch within 89 degrees, a few presses per tick. Anything else is
        /// clamped, NaN becomes 0. A valid input comes back bit for bit, so an honest
        /// peer never sees its own input corrected.</summary>
        public static MoveInput Sanitize(in MoveInput m)
        {
            MoveInput s = m;
            s.Move = new Stride.Core.Mathematics.Vector2(Limit(m.Move.X, 1f), Limit(m.Move.Y, 1f));
            s.Yaw = Limit(m.Yaw, MaxYaw);
            s.Pitch = Limit(m.Pitch, 89f);
            s.JumpPresses = Math.Min(m.JumpPresses, MaxPresses);
            s.AttackPresses = Math.Min(m.AttackPresses, MaxPresses);
            return s;
        }

        static float Limit(float v, float bound)
            => float.IsNaN(v) ? 0f : v > bound ? bound : v < -bound ? -bound : v;

        /// <summary>Bit for bit, the way the simulation would see the difference.</summary>
        public static bool Same(in MoveInput a, in MoveInput b)
            => Bits(a.Move.X) == Bits(b.Move.X) && Bits(a.Move.Y) == Bits(b.Move.Y) &&
               Bits(a.Yaw) == Bits(b.Yaw) && Bits(a.Pitch) == Bits(b.Pitch) &&
               a.JumpPresses == b.JumpPresses && a.AttackPresses == b.AttackPresses &&
               a.JumpHeld == b.JumpHeld && a.CrouchPressed == b.CrouchPressed &&
               a.Sprint == b.Sprint && a.Crouch == b.Crouch && a.Blaster == b.Blaster;

        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

        /// <summary>
        /// What a body most likely does in a tick nobody has confirmed yet: what
        /// it did last, minus the presses. Repeating a press would jump twice.
        /// </summary>
        public static MoveInput Hold(in MoveInput last)
        {
            MoveInput m = last;
            m.JumpPresses = 0;
            m.AttackPresses = 0;
            m.CrouchPressed = false;
            return m;
        }

        public static void WriteEvent(ref Writer w, in NetEvent e)
        {
            w.Byte(e.Join ? (byte)1 : (byte)0);
            w.Int(e.NetId);
            if (!e.Join)
                return;
            w.Byte((byte)e.Slot);
            w.Text(e.Name);
        }

        public static NetEvent ReadEvent(ref Reader r)
        {
            var e = new NetEvent { Join = r.Byte() != 0, NetId = r.Int() };
            if (e.Join)
            {
                e.Slot = r.Byte();
                e.Name = r.Text();
            }
            return e;
        }
    }

    /// <summary>Appends little-endian values to a growing buffer.</summary>
    public struct Writer
    {
        readonly ArrayBufferWriter<byte> into;

        public Writer(ArrayBufferWriter<byte> into)
        {
            this.into = into;
        }

        public ArrayBufferWriter<byte> Buffer => into;

        public void Byte(byte v)
        {
            into.GetSpan(1)[0] = v;
            into.Advance(1);
        }

        public void Int(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(into.GetSpan(4), v);
            into.Advance(4);
        }

        public void ULong(ulong v)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(into.GetSpan(8), v);
            into.Advance(8);
        }

        public void Float(float v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(into.GetSpan(4), v);
            into.Advance(4);
        }

        public void Double(double v)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(into.GetSpan(8), v);
            into.Advance(8);
        }

        public void Bytes(ReadOnlySpan<byte> v)
        {
            v.CopyTo(into.GetSpan(v.Length));
            into.Advance(v.Length);
        }

        /// <summary>UTF-8, at most 255 bytes, cut on a character boundary.</summary>
        public void Text(string v)
        {
            v ??= "";
            byte[] bytes = Encoding.UTF8.GetBytes(v);
            int length = Math.Min(bytes.Length, 255);
            while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
                length--;
            Byte((byte)length);
            Bytes(bytes.AsSpan(0, length));
        }
    }

    /// <summary>Reads what a Writer wrote; past the end every read gives zero and sets Failed.</summary>
    public ref struct Reader
    {
        readonly ReadOnlySpan<byte> data;
        int pos;

        public Reader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            pos = 0;
            Failed = false;
        }

        public bool Failed { get; private set; }
        public int Left => data.Length - pos;

        bool Take(int n)
        {
            if (pos + n <= data.Length)
                return true;
            Failed = true;
            pos = data.Length;
            return false;
        }

        public byte Byte() => Take(1) ? data[pos++] : (byte)0;

        public int Int()
        {
            if (!Take(4)) return 0;
            int v = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            pos += 4;
            return v;
        }

        public ulong ULong()
        {
            if (!Take(8)) return 0;
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);
            pos += 8;
            return v;
        }

        public float Float()
        {
            if (!Take(4)) return 0f;
            float v = BinaryPrimitives.ReadSingleLittleEndian(data[pos..]);
            pos += 4;
            return v;
        }

        public double Double()
        {
            if (!Take(8)) return 0.0;
            double v = BinaryPrimitives.ReadDoubleLittleEndian(data[pos..]);
            pos += 8;
            return v;
        }

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || !Take(n)) return ReadOnlySpan<byte>.Empty;
            ReadOnlySpan<byte> v = data.Slice(pos, n);
            pos += n;
            return v;
        }

        public string Text() => Encoding.UTF8.GetString(Bytes(Byte()));
    }
}
