// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Security.Cryptography;
using System.Text;

namespace Satisvampory.Engine.Security
{
    /// <summary>
    /// Chest passwords, kept only as a salted PBKDF2-SHA256 hash: "v1:iterations:salt:hash" (base64). The text a
    /// player types is never stored or logged. A chest lock is a game convenience, not real security: players are
    /// told not to reuse a real password.
    /// </summary>
    public static class Passwords
    {
        const int Iterations = 20000;
        const int SaltBytes = 16, HashBytes = 32;

        public const int MinLength = 1, MaxLength = 64;

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var hash = Derive(password, salt, Iterations);
            return $"v1:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;
            var parts = stored.Split(':');
            if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                return CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations, expected.Length), expected);
            }
            catch (FormatException) { return false; }
        }

        static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes)
        {
            using var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password ?? ""), salt, iterations, HashAlgorithmName.SHA256);
            return kdf.GetBytes(length);
        }
    }
}
