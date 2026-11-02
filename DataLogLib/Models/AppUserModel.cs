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
    /// A user of the web UI (table appuser, schema 15).
    /// </summary>
    public class AppUserModel
    {
        public const string RoleAdmin = "admin";
        public const string RoleViewer = "viewer";

        public int Id { get; set; }
        public string Name { get; set; } = "";

        /// <summary>The PBKDF2 hash of the password (see PasswordHashing), never the password itself.</summary>
        public string PasswordHash { get; set; } = "";
        public string Role { get; set; } = RoleViewer;

        /// <summary>Changes with the password or the role; a session with another stamp is signed out.</summary>
        public string SecurityStamp { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }

        public bool IsAdmin => Role == RoleAdmin;

        public static bool IsValidRole(string role) => role == RoleAdmin || role == RoleViewer;

        public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
    }
}
