using System;
using System.Security.Cryptography;
using System.Text;

namespace KotH.Shared.Net
{
    /// <summary>A private round's password as the host keeps it: a key stretched from it (PBKDF2-SHA256)
    /// with a random salt. The password never travels: the host sends salt and a fresh nonce per peer
    /// (Map), the peer answers with HMAC-SHA256(key, nonce) in its Hello, the host compares in constant
    /// time. A recorded answer fits no other nonce.</summary>
    public sealed class PasswordKey
    {
        public const int SaltSize = 16, NonceSize = 16, ProofSize = 32, KeySize = 32;
        const int Rounds = 100_000;

        public readonly byte[] Salt;
        public readonly byte[] Key;

        public PasswordKey(byte[] salt, byte[] key)
        {
            Salt = salt;
            Key = key;
        }

        /// <summary>The host's key for a new private round.</summary>
        public static PasswordKey Make(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            return new PasswordKey(salt, Derive(password, salt));
        }

        public static byte[] Derive(string password, ReadOnlySpan<byte> salt)
            => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, Rounds, HashAlgorithmName.SHA256, KeySize);

        public static byte[] Nonce() => RandomNumberGenerator.GetBytes(NonceSize);

        /// <summary>What a peer that knows the password answers to the nonce.</summary>
        public static byte[] Proof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce) => HMACSHA256.HashData(key, nonce);

        public bool Check(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> proof)
            => proof.Length == ProofSize && CryptographicOperations.FixedTimeEquals(Proof(Key, nonce), proof);
    }
}
