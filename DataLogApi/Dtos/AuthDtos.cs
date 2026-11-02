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

namespace DataLogApi.Dtos
{
    public class AuthUserDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
    }

    public class AuthStatusDto
    {
        /// <summary>No user exists yet: the first visitor creates the administrator.</summary>
        public bool SetupRequired { get; set; }

        /// <summary>The signed-in user, null when not signed in.</summary>
        public AuthUserDto User { get; set; }
    }

    public class LoginDto
    {
        public string Name { get; set; }
        public string Password { get; set; }
    }

    public class PasswordChangeDto
    {
        public string Current { get; set; }
        public string New { get; set; }
    }

    public class AppUserDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
    }

    public class AppUserWriteDto
    {
        /// <summary>Only when creating a user.</summary>
        public string Name { get; set; }

        /// <summary>admin or viewer; null keeps the role.</summary>
        public string Role { get; set; }

        /// <summary>A new password; null or empty keeps the password.</summary>
        public string Password { get; set; }
    }
}
