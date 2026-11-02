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

namespace DataLogLib.Models
{
    /// <summary>
    /// The stored values of the OPC UA security properties of a data source.
    /// </summary>
    public static class OpcUaSecurityValues
    {
        public const string ModeNone = "none";
        public const string ModeSign = "sign";
        public const string ModeSignAndEncrypt = "signandencrypt";

        public const string PolicyBest = "best";

        public const string TrustAny = "any";
        public const string TrustPinned = "pinned";

        /// <summary>The policies a user can choose besides <see cref="PolicyBest"/>, by their OPC UA names.</summary>
        public static readonly IReadOnlyList<string> Policies =
        [
            "Aes256_Sha256_RsaPss",
            "Basic256Sha256",
            "Aes128_Sha256_RsaOaep"
        ];

        public static readonly IReadOnlyList<string> Modes = [ModeNone, ModeSign, ModeSignAndEncrypt];

        public static readonly IReadOnlyList<string> Trusts = [TrustAny, TrustPinned];

        /// <summary>A thumbprint in the stored form: hex digits only, upper case.</summary>
        public static string NormalizeThumbprint(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                return "";
            }

            return new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        }
    }
}
