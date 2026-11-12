using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KotH.Shared.Net
{
    /// <summary>One direct-IP connection's encryption. The peer sends an ephemeral ECDH P-256 key (Hello),
    /// the host its lasting public key, an ephemeral one and its signature over both (Reply); the peer checks
    /// the signature and, from an invite, the fingerprint. HKDF-SHA256 gives a key per direction; each packet
    /// is AES-GCM sealed with a counter in its nonce, and a counter seen before (or too old) is dropped.</summary>
    public sealed class SecureLink : IDisposable
    {
        public const byte HelloKind = 0xC1, ReplyKind = 0xC2, SealedKind = 0xC3;

        /// <summary>Bytes a sealed packet adds: kind, counter, tag.</summary>
        public const int Overhead = 1 + 8 + TagSize;

        const int TagSize = 16, PointSize = 65, Window = 64;
        static readonly byte[] Context = "KotH direct-IP handshake 1"u8.ToArray();

        readonly ECDiffieHellman mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        readonly byte[] myPoint;
        readonly bool hosting;
        readonly byte[] nonce = new byte[12];
        AesGcm sealer, opener;
        ulong sent, newest, seen;   // seen: the window of counters below newest already opened

        SecureLink(bool hosting)
        {
            this.hosting = hosting;
            myPoint = Point(mine);
        }

        /// <summary>Both keys are agreed: packets may be sealed and opened.</summary>
        public bool Ready => sealer != null;

        /// <summary>The peer side: the host's fingerprint once its reply checked out.</summary>
        public string HostFingerprint { get; private set; }

        /// <summary>A peer starts: the link and its Hello to send (reliably).</summary>
        public static SecureLink Peer(out byte[] hello)
        {
            var link = new SecureLink(hosting: false);
            hello = new byte[1 + PointSize];
            hello[0] = HelloKind;
            link.myPoint.CopyTo(hello, 1);
            return link;
        }

        /// <summary>The host answers a Hello: the link and the Reply to send (reliably); null for no Hello.</summary>
        public static SecureLink Host(HostKey key, ReadOnlySpan<byte> hello, out byte[] reply)
        {
            reply = null;
            if (hello.Length != 1 + PointSize || hello[0] != HelloKind)
                return null;
            var link = new SecureLink(hosting: true);
            ReadOnlySpan<byte> theirs = hello[1..];
            if (!link.Agree(theirs, peerPoint: theirs, hostPoint: link.myPoint))
            {
                link.Dispose();
                return null;
            }
            byte[] signature = key.Key.SignData(Signed(theirs, link.myPoint), HashAlgorithmName.SHA256);
            var w = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new Writer(w);
            writer.Byte(ReplyKind);
            writer.Int(key.Public.Length);
            writer.Bytes(key.Public);
            writer.Bytes(link.myPoint);
            writer.Byte((byte)signature.Length);
            writer.Bytes(signature);
            reply = w.WrittenSpan.ToArray();
            return link;
        }

        /// <summary>The peer takes the host's Reply: signature right and, with a pinned fingerprint (the
        /// invite's), the host's key the expected one. False with why not; then the connection must end.</summary>
        public bool TakeReply(ReadOnlySpan<byte> reply, string pinned, out string why)
        {
            var r = new Reader(reply);
            why = "The host's handshake was broken";
            if (hosting || Ready || r.Byte() != ReplyKind)
                return false;
            ReadOnlySpan<byte> hostKey = r.Bytes(Math.Clamp(r.Int(), 0, 512));
            ReadOnlySpan<byte> hostPoint = r.Bytes(PointSize);
            ReadOnlySpan<byte> signature = r.Bytes(r.Byte());
            if (r.Failed)
                return false;
            string fingerprint = HostKey.FingerprintOf(hostKey);
            if (pinned != null && !string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                why = "The host's key does not match the invite";
                return false;
            }
            try
            {
                using var verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(hostKey, out _);
                if (!verifier.VerifyData(Signed(myPoint, hostPoint), signature, HashAlgorithmName.SHA256))
                {
                    why = "The host's signature is wrong";
                    return false;
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
            if (!Agree(hostPoint, peerPoint: myPoint, hostPoint: hostPoint))
                return false;
            HostFingerprint = fingerprint;
            why = null;
            return true;
        }

        // The shared secret with their point, then one key per direction, bound to both points.
        bool Agree(ReadOnlySpan<byte> theirs, ReadOnlySpan<byte> peerPoint, ReadOnlySpan<byte> hostPoint)
        {
            byte[] secret;
            try
            {
                using var other = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = theirs.Slice(1, 32).ToArray(), Y = theirs.Slice(33, 32).ToArray() },
                });
                if (theirs[0] != 4)
                    return false;
                secret = mine.DeriveRawSecretAgreement(other.PublicKey);
            }
            catch (CryptographicException)
            {
                return false;
            }
            byte[] salt = new byte[2 * PointSize];
            peerPoint.CopyTo(salt);
            hostPoint.CopyTo(salt.AsSpan(PointSize));
            byte[] toHost = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "KotH peer to host"u8.ToArray());
            byte[] toPeer = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "KotH host to peer"u8.ToArray());
            CryptographicOperations.ZeroMemory(secret);
            sealer = new AesGcm(hosting ? toPeer : toHost, TagSize);
            opener = new AesGcm(hosting ? toHost : toPeer, TagSize);
            CryptographicOperations.ZeroMemory(toHost);
            CryptographicOperations.ZeroMemory(toPeer);
            return true;
        }

        static byte[] Signed(ReadOnlySpan<byte> peerPoint, ReadOnlySpan<byte> hostPoint)
        {
            byte[] data = new byte[Context.Length + 2 * PointSize];
            Context.CopyTo(data, 0);
            peerPoint.CopyTo(data.AsSpan(Context.Length));
            hostPoint.CopyTo(data.AsSpan(Context.Length + PointSize));
            return data;
        }

        static byte[] Point(ECDiffieHellman key)
        {
            ECParameters p = key.ExportParameters(false);
            byte[] point = new byte[PointSize];
            point[0] = 4;
            p.Q.X.CopyTo(point, 1);
            p.Q.Y.CopyTo(point, 33);
            return point;
        }

        // Direction in the first byte (who seals), the counter in the last eight.
        void Nonce(bool fromHost, ulong counter)
        {
            nonce[0] = fromHost ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        }

        /// <summary>Seals a message into into (at least Overhead bytes longer); its length.</summary>
        public int Seal(ReadOnlySpan<byte> plain, Span<byte> into)
        {
            ulong counter = ++sent;
            into[0] = SealedKind;
            BinaryPrimitives.WriteUInt64LittleEndian(into[1..], counter);
            Nonce(hosting, counter);
            sealer.Encrypt(nonce, plain, into.Slice(9, plain.Length), into.Slice(9 + plain.Length, TagSize), into[..9]);
            return plain.Length + Overhead;
        }

        /// <summary>Opens a sealed packet into into (packet.Length - Overhead bytes at least): the message's
        /// length, or -1 when it is forged, damaged or seen before.</summary>
        public int Open(ReadOnlySpan<byte> packet, Span<byte> into)
        {
            if (!Ready || packet.Length < Overhead || packet[0] != SealedKind)
                return -1;
            ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(packet[1..]);
            if (counter == 0 || counter <= newest && (newest - counter >= Window || (seen >> (int)(newest - counter) & 1) != 0))
                return -1;
            int length = packet.Length - Overhead;
            Nonce(!hosting, counter);
            try
            {
                opener.Decrypt(nonce, packet.Slice(9, length), packet.Slice(9 + length, TagSize), into[..length], packet[..9]);
            }
            catch (AuthenticationTagMismatchException)
            {
                return -1;
            }
            if (counter > newest)
            {
                ulong shift = counter - newest;
                seen = shift >= Window ? 1UL : seen << (int)shift | 1UL;
                newest = counter;
            }
            else
                seen |= 1UL << (int)(newest - counter);
            return length;
        }

        public void Dispose()
        {
            mine.Dispose();
            sealer?.Dispose();
            opener?.Dispose();
        }
    }
}
