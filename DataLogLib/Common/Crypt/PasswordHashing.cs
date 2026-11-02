/*
DataLogXE - A simple and high performance data logger.
Copyright (C) 2026 datalogxe@gmail.com

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataLogLib.Common.Crypt
{
    /// <summary>
    /// Hashes the passwords of the UI users with PBKDF2-SHA256: "pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;"
    /// (salt and hash base64). The iteration count is part of the value, so it can grow later (<see cref="NeedsRehash"/>).
    /// </summary>
    public static class PasswordHashing
    {
        private const string Scheme = "pbkdf2-sha256";
        private const int SaltBytes = 16;
        private const int HashBytes = 32;

        /// <summary>About 0.3 s on a Raspberry Pi 3, a few ms on a PC.</summary>
        public const int DefaultIterations = 100_000;

        public static string Hash(string password, int iterations = DefaultIterations)
        {
            ArgumentNullException.ThrowIfNull(password);
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var hash = Derive(password, salt, iterations);
            return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture),
                Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        /// <summary>
        /// Whether the password matches the stored hash; false for a broken or unknown value.
        /// </summary>
        public static bool Verify(string password, string stored)
        {
            if (password == null || !TryParse(stored, out var iterations, out var salt, out var expected))
            {
                return false;
            }

            var actual = Derive(password, salt, iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        /// <summary>
        /// Takes as long as a real check, so a login with an unknown name does not answer faster.
        /// </summary>
        public static void VerifyDummy(string password)
        {
            Derive(password ?? "", new byte[SaltBytes], DefaultIterations);
        }

        public static bool NeedsRehash(string stored)
        {
            return !TryParse(stored, out var iterations, out _, out _) || iterations < DefaultIterations;
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                HashAlgorithmName.SHA256, HashBytes);
        }

        private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
        {
            iterations = 0;
            salt = null;
            hash = null;
            var parts = (stored ?? "").Split('$');
            if (parts.Length != 4 || parts[0] != Scheme
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
                || iterations < 1000 || iterations > 10_000_000)
            {
                return false;
            }

            try
            {
                salt = Convert.FromBase64String(parts[2]);
                hash = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }

            return salt.Length >= 8 && hash.Length == HashBytes;
        }
    }
}
