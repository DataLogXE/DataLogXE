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

using System.Security.Cryptography;
using System.Text;

namespace DataLogLib.Common.Crypt
{
    /// <summary>
    /// The password format up to 0.0.199: DES with a key and IV taken from a hash of the data source GUID, which is
    /// stored next to the password. That only hides the password; anyone with the database can read it. Kept only to
    /// read old values once and to rewrite them with <see cref="SecretProtector"/>.
    /// </summary>
    internal static class LegacyDesCrypt
    {
        /// <summary>
        /// Writes the old format; only the tests need it, to build old values.
        /// </summary>
        internal static string Encrypt(string text, string salt)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            try
            {
                using var algorithm = Create(salt, out var key, out var iv);
                using var transform = algorithm.CreateEncryptor(key, iv);
                byte[] inputBuffer = Encoding.Unicode.GetBytes(text);
                byte[] outputBuffer = transform.TransformFinalBlock(inputBuffer, 0, inputBuffer.Length);
                return Convert.ToBase64String(outputBuffer);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Reads a value of the old format; empty when it cannot be read.
        /// </summary>
        internal static string Decrypt(string text, string salt)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            try
            {
                using var algorithm = Create(salt, out var key, out var iv);
                using var transform = algorithm.CreateDecryptor(key, iv);
                byte[] inputbuffer = Convert.FromBase64String(text);
                byte[] outputBuffer = transform.TransformFinalBlock(inputbuffer, 0, inputbuffer.Length);
                return Encoding.Unicode.GetString(outputBuffer);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static SymmetricAlgorithm Create(string salt, out byte[] key, out byte[] iv)
        {
            var h = GetHash(salt ?? "").Substring(32);
            key = Encoding.ASCII.GetBytes(h.Substring(h.Length - 8));
            iv = Encoding.ASCII.GetBytes(h.Substring(0, 8));
#pragma warning disable CA5351 // DES only reads the old format
            return DES.Create();
#pragma warning restore CA5351
        }

        private static string GetHash(string salt)
        {
            var inputBytes = Encoding.UTF8.GetBytes(salt);
            var inputHash = SHA256.HashData(inputBytes);
            return Convert.ToHexString(inputHash);
        }
    }
}
