using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace KotH.Shared.Net
{
    /// <summary>A direct-IP host's lasting identity: an ECDSA P-256 key pair in host_key.pem (made on the first
    /// hosting, kept so the invite stays valid), whose public key signs every handshake. Its fingerprint, the
    /// first 16 bytes of SHA-256 over the public key in Base32, travels in the invite code (key pinning).</summary>
    public sealed class HostKey : IDisposable
    {
        public readonly ECDsa Key;

        /// <summary>The public key as SubjectPublicKeyInfo, as the handshake sends it.</summary>
        public readonly byte[] Public;
        public readonly string Fingerprint;

        HostKey(ECDsa key)
        {
            Key = key;
            Public = key.ExportSubjectPublicKeyInfo();
            Fingerprint = FingerprintOf(Public);
        }

        /// <summary>Where the key lives: the user's local data (~/.local/share/KotH, AppData on Windows).</summary>
        public static string DefaultPath
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotH", "host_key.pem");

        /// <summary>The key from the file, or a new one written there (readable by the user only).</summary>
        public static HostKey LoadOrCreate(string path = null)
        {
            path ??= DefaultPath;
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                if (File.Exists(path))
                {
                    key.ImportFromPem(File.ReadAllText(path));
                    return new HostKey(key);
                }
            }
            catch (Exception e) when (e is IOException or CryptographicException or ArgumentException)
            {
                Console.WriteLine($"[net] host key unreadable, a new one replaces it: {e.Message}");
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new HostKey(key);
        }

        /// <summary>A key made in memory, for probes.</summary>
        public static HostKey Fresh() => new HostKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));

        public static string FingerprintOf(ReadOnlySpan<byte> publicKey) => Base32(SHA256.HashData(publicKey).AsSpan(0, 16));

        // RFC 4648 Base32 in lower case, without padding: 16 bytes give 26 characters.
        static string Base32(ReadOnlySpan<byte> data)
        {
            const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
            var s = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = buffer << 8 | b;
                bits += 8;
                while (bits >= 5)
                {
                    s.Append(Alphabet[buffer >> (bits - 5) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0)
                s.Append(Alphabet[buffer << (5 - bits) & 31]);
            return s.ToString();
        }

        /// <summary>The invite code for this host: koth://join?ip=…&amp;port=…&amp;key=….</summary>
        public string Invite(string address, int port) => Invites.Make(address, port, Fingerprint);

        public void Dispose() => Key.Dispose();
    }

    /// <summary>Invite codes for direct IP: koth://join?ip=1.2.3.4&amp;port=7777&amp;key=FINGERPRINT.</summary>
    public static class Invites
    {
        public static string Make(string address, int port, string key)
            => $"koth://join?ip={address}&port={port}&key={key}";

        /// <summary>Address, port and key of a code; false when it is none.</summary>
        public static bool TryParse(string code, out string address, out int port, out string key)
        {
            address = key = null;
            port = LiteNetTransport.DefaultPort;
            code = code?.Trim();
            const string Head = "koth://join?";
            if (code == null || !code.StartsWith(Head, StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (string part in code[Head.Length..].Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                string value = Uri.UnescapeDataString(part[(eq + 1)..]);
                switch (part[..eq].ToLowerInvariant())
                {
                    case "ip": address = value.Trim('[', ']'); break;
                    case "port": if (!int.TryParse(value, out port) || port <= 0 || port > 65535) return false; break;
                    case "key": key = value.ToLowerInvariant(); break;
                }
            }
            return !string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(key);
        }

        /// <summary>The first IPv4 address of a running network card, for the code; others may need the public one.</summary>
        public static string LocalAddress()
        {
            try
            {
                foreach (NetworkInterface card in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (card.OperationalStatus != OperationalStatus.Up || card.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (UnicastIPAddressInformation a in card.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                            return a.Address.ToString();
                }
            }
            catch (NetworkInformationException)
            {
            }
            return "127.0.0.1";
        }
    }
}
