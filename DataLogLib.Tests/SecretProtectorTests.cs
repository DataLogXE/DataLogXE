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

using DataLogLib.Common.Crypt;
using System.Security.Cryptography;
using Xunit;

namespace DataLogLib.Tests
{
    public class SecretProtectorTests
    {
        private static readonly byte[] Key = RandomNumberGenerator.GetBytes(MasterKey.KeyBytes);
        private const string Guid1 = "8D5C1A55F0E54B6E9C1A2B3C4D5E6F70";

        [Fact]
        public void Protect_and_unprotect_give_the_text_back()
        {
            var stored = SecretProtector.Protect("pässwörd ;=\"", Guid1, Key);

            Assert.StartsWith(SecretProtector.Prefix, stored);
            Assert.DoesNotContain("pässwörd", stored);
            Assert.Equal("pässwörd ;=\"", SecretProtector.Unprotect(stored, Guid1, Key));
        }

        [Fact]
        public void The_same_text_is_stored_differently_each_time()
        {
            Assert.NotEqual(SecretProtector.Protect("secret", Guid1, Key), SecretProtector.Protect("secret", Guid1, Key));
        }

        [Fact]
        public void An_empty_text_stays_empty()
        {
            Assert.Equal("", SecretProtector.Protect("", Guid1, Key));
            Assert.Equal("", SecretProtector.Protect(null, Guid1, Key));
            Assert.Equal("", SecretProtector.Unprotect("", Guid1));
            Assert.Equal("", SecretProtector.Unprotect(null, Guid1));
        }

        [Fact]
        public void A_changed_value_does_not_open()
        {
            var stored = SecretProtector.Protect("secret", Guid1, Key);
            var bytes = Convert.FromBase64String(stored.Substring(SecretProtector.Prefix.Length));
            bytes[^1] ^= 0x01;
            var changed = SecretProtector.Prefix + Convert.ToBase64String(bytes);

            Assert.ThrowsAny<CryptographicException>(() => SecretProtector.Unprotect(changed, Guid1, Key));
        }

        [Fact]
        public void A_value_copied_into_another_row_does_not_open()
        {
            var stored = SecretProtector.Protect("secret", Guid1, Key);

            Assert.ThrowsAny<CryptographicException>(() => SecretProtector.Unprotect(stored, "OTHERGUID", Key));
        }

        [Fact]
        public void A_value_of_another_key_does_not_open()
        {
            var stored = SecretProtector.Protect("secret", Guid1, Key);
            var otherKey = RandomNumberGenerator.GetBytes(MasterKey.KeyBytes);

            Assert.ThrowsAny<CryptographicException>(() => SecretProtector.Unprotect(stored, Guid1, otherKey));
        }

        [Fact]
        public void A_broken_value_is_a_cryptographic_error()
        {
            Assert.ThrowsAny<CryptographicException>(() => SecretProtector.Unprotect("v2:###", Guid1, Key));
            Assert.ThrowsAny<CryptographicException>(() => SecretProtector.Unprotect("v2:AAAA", Guid1, Key));
        }

        [Fact]
        public void A_value_of_the_old_format_is_read_with_des()
        {
            var legacy = LegacyDesCrypt.Encrypt("old secret", Guid1);

            Assert.False(SecretProtector.IsProtected(legacy));
            Assert.Equal("old secret", SecretProtector.Unprotect(legacy, Guid1));
        }

        [Fact]
        public void The_fingerprint_changes_with_the_secret_and_does_not_contain_it()
        {
            var a = SecretProtector.Fingerprint("secret-a");

            Assert.Equal(a, SecretProtector.Fingerprint("secret-a"));
            Assert.NotEqual(a, SecretProtector.Fingerprint("secret-b"));
            Assert.DoesNotContain("secret", a);
            Assert.Equal("", SecretProtector.Fingerprint(""));
        }
    }

    public class MasterKeyTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "masterkey-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        [Fact]
        public void A_missing_file_is_created_and_read_again()
        {
            var path = Path.Combine(_folder, MasterKey.FileName);

            var created = MasterKey.Load(null, path);
            var read = MasterKey.Load("", path);

            Assert.Equal(MasterKey.KeyBytes, created.Length);
            Assert.Equal(created, read);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
            Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
        }

        [Fact]
        public void The_environment_value_wins_over_the_file()
        {
            var path = Path.Combine(_folder, MasterKey.FileName);
            MasterKey.Load(null, path);
            var key = RandomNumberGenerator.GetBytes(MasterKey.KeyBytes);

            Assert.Equal(key, MasterKey.Load(Convert.ToBase64String(key), path));
        }

        [Fact]
        public void A_key_of_the_wrong_length_or_format_stops_the_start()
        {
            var path = Path.Combine(_folder, MasterKey.FileName);

            Assert.Throws<InvalidOperationException>(() => MasterKey.Load(Convert.ToBase64String(new byte[16]), path));
            Assert.Throws<InvalidOperationException>(() => MasterKey.Load("no base64!", path));
            Assert.False(File.Exists(path));
        }
    }
}
