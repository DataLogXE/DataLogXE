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
    /// Protects stored secrets (the data source passwords) with AES-256-GCM and the <see cref="MasterKey"/>. A value is
    /// "v2:" + base64(nonce | ciphertext | tag); the associated data (the data source GUID) binds it to its row, so a
    /// value copied into another row does not open. Values without the prefix are the DES format up to 0.0.199.
    /// </summary>
    public static class SecretProtector
    {
        public const string Prefix = "v2:";
        private const int NonceBytes = 12;
        private const int TagBytes = 16;

        public static bool IsProtected(string stored)
        {
            return stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static string Protect(string plain, string associatedData)
        {
            return Protect(plain, associatedData, MasterKey.Get());
        }

        internal static string Protect(string plain, string associatedData, byte[] key)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return string.Empty;
            }

            var plainBytes = Encoding.UTF8.GetBytes(plain);
            var buffer = new byte[NonceBytes + plainBytes.Length + TagBytes];
            var nonce = buffer.AsSpan(0, NonceBytes);
            var cipher = buffer.AsSpan(NonceBytes, plainBytes.Length);
            var tag = buffer.AsSpan(NonceBytes + plainBytes.Length, TagBytes);
            RandomNumberGenerator.Fill(nonce);
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Encrypt(nonce, plainBytes, cipher, tag, Aad(associatedData));
            }

            CryptographicOperations.ZeroMemory(plainBytes);
            return Prefix + Convert.ToBase64String(buffer);
        }

        /// <summary>
        /// The plain text of a stored value; values of the old format are read with <see cref="LegacyDesCrypt"/>.
        /// Throws <see cref="CryptographicException"/> when a protected value was changed, belongs to another row or
        /// was written with another key.
        /// </summary>
        public static string Unprotect(string stored, string associatedData)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return string.Empty;
            }

            if (!IsProtected(stored))
            {
                return LegacyDesCrypt.Decrypt(stored, associatedData);
            }

            return Unprotect(stored, associatedData, MasterKey.Get());
        }

        internal static string Unprotect(string stored, string associatedData, byte[] key)
        {
            byte[] buffer;
            try
            {
                buffer = Convert.FromBase64String(stored.Substring(Prefix.Length));
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("The stored secret is not valid base64.", ex);
            }

            if (buffer.Length < NonceBytes + TagBytes)
            {
                throw new CryptographicException("The stored secret is too short.");
            }

            var cipherLength = buffer.Length - NonceBytes - TagBytes;
            var plain = new byte[cipherLength];
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Decrypt(
                    buffer.AsSpan(0, NonceBytes),
                    buffer.AsSpan(NonceBytes, cipherLength),
                    buffer.AsSpan(NonceBytes + cipherLength, TagBytes),
                    plain,
                    Aad(associatedData));
            }

            var text = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return text;
        }

        /// <summary>
        /// A short hash of a secret, to notice a change without keeping the secret in a compare string.
        /// </summary>
        public static string Fingerprint(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return string.Empty;
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain)), 0, 8);
        }

        private static byte[] Aad(string associatedData)
        {
            return Encoding.UTF8.GetBytes(associatedData ?? "");
        }
    }
}
