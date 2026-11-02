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

using Serilog;
using System.Security.Cryptography;

namespace DataLogLib.Common.Crypt
{
    /// <summary>
    /// The 32 byte key that protects the stored data source passwords. It comes from DATALOG_MASTER_KEY (base64) when
    /// set, otherwise from the file master.key in the data folder (DATA_PATH, default /app/data, a volume in Docker),
    /// which is created on the first start. It is never written to the database or the log.
    /// </summary>
    public static class MasterKey
    {
        public const int KeyBytes = 32;
        public const string EnvironmentVariable = "DATALOG_MASTER_KEY";
        public const string FileName = "master.key";

        private static readonly object Sync = new();
        private static byte[] _key;

        /// <summary>
        /// The key, loaded or created on the first call.
        /// </summary>
        public static byte[] Get()
        {
            lock (Sync)
            {
                return _key ??= Load(Environment.GetEnvironmentVariable(EnvironmentVariable), DefaultFilePath);
            }
        }

        /// <summary>
        /// The file the key is read from or created in when DATALOG_MASTER_KEY is not set.
        /// </summary>
        public static string DefaultFilePath
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("DATA_PATH");
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = Environment.GetEnvironmentVariable("DATABASE_PATH");
                }

                if (string.IsNullOrWhiteSpace(root))
                {
                    root = "/app/data";
                }

                return Path.Combine(root, FileName);
            }
        }

        /// <summary>
        /// Sets the key of this process; for the tests.
        /// </summary>
        internal static void SetForTests(byte[] key)
        {
            lock (Sync)
            {
                _key = key;
            }
        }

        /// <summary>
        /// The key from the environment value when given, otherwise from the file, which is created when missing.
        /// </summary>
        internal static byte[] Load(string environmentValue, string filePath)
        {
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return Parse(environmentValue, EnvironmentVariable);
            }

            if (File.Exists(filePath))
            {
                return Parse(File.ReadAllText(filePath), filePath);
            }

            return Create(filePath);
        }

        private static byte[] Parse(string text, string origin)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(text.Trim());
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"The master key in {origin} is not base64.");
            }

            if (key.Length != KeyBytes)
            {
                throw new InvalidOperationException(
                    $"The master key in {origin} has {key.Length} bytes instead of {KeyBytes}.");
            }

            return key;
        }

        private static byte[] Create(string filePath)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var key = RandomNumberGenerator.GetBytes(KeyBytes);
            var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(Convert.ToBase64String(key));
            }

            try
            {
                // A second process that created the file first wins; both then use its key.
                File.Move(temp, filePath, overwrite: false);
            }
            catch (IOException) when (File.Exists(filePath))
            {
                File.Delete(temp);
                return Parse(File.ReadAllText(filePath), filePath);
            }

            Log.Information("Created a new master key for the stored passwords in {MasterKeyFile}", filePath);
            return key;
        }
    }
}
