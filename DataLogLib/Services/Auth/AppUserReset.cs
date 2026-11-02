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

using DataAccessLib.Models;
using DataLogLib.Common.Crypt;
using DataLogLib.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Cryptography;

namespace DataLogLib.Services.Auth
{
    /// <summary>
    /// "--reset-password &lt;name&gt;": sets a new random password for a UI user without the UI, e.g. when the
    /// administrator password is lost (docker exec ... dotnet DataLogApi.dll --reset-password admin). A missing user
    /// is created as administrator. The other sessions of the user end.
    /// </summary>
    public static class AppUserReset
    {
        public const string ArgName = "--reset-password";

        public static bool TryRun(string[] args, IConfiguration configuration, out int exitCode)
        {
            exitCode = 0;
            var index = Array.IndexOf(args ?? [], ArgName);
            if (index < 0)
            {
                return false;
            }

            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                Console.Error.WriteLine($"Usage: dotnet DataLogApi.dll {ArgName} <user name>");
                exitCode = 2;
                return true;
            }

            try
            {
                var name = args[index + 1].Trim();
                var password = NewPassword();
                var created = Reset(new DataConnectionInfo(configuration).ConnectionString, name, password);
                Console.WriteLine(created
                    ? $"Created the administrator \"{name}\" with the password: {password}"
                    : $"New password of \"{name}\": {password}");
                Console.WriteLine("Sign in and change it under Settings > Account.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Password reset failed: " + ex.Message);
                exitCode = 1;
            }

            return true;
        }

        /// <summary>True when the user did not exist and was created as administrator.</summary>
        internal static bool Reset(string connectionString, string name, string password)
        {
            using var dataSource = NpgsqlDataSource.Create(connectionString);
            using var connection = dataSource.OpenConnection();
            using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE appuser SET passwordhash = @hash, securitystamp = @stamp WHERE lower(name) = lower(@name)";
            update.Parameters.AddWithValue("hash", PasswordHashing.Hash(password));
            update.Parameters.AddWithValue("stamp", AppUserModel.NewSecurityStamp());
            update.Parameters.AddWithValue("name", name);
            if (update.ExecuteNonQuery() > 0)
            {
                return false;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO appuser (name, passwordhash, role, securitystamp) VALUES (@name, @hash, 'admin', @stamp)";
            insert.Parameters.AddWithValue("hash", PasswordHashing.Hash(password));
            insert.Parameters.AddWithValue("stamp", AppUserModel.NewSecurityStamp());
            insert.Parameters.AddWithValue("name", name);
            insert.ExecuteNonQuery();
            return true;
        }

        private static string NewPassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            return RandomNumberGenerator.GetString(chars, 16);
        }
    }
}
