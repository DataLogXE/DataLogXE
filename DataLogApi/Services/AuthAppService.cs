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

using DataLogApi.Dtos;
using DataLogLib.Common.Crypt;
using DataLogLib.Models;
using DataLogLib.Services.Auth;
using DataLogService;
using Serilog;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace DataLogApi.Services
{
    /// <summary>
    /// The users of the web UI: first-run setup, login with throttling, own password, and the user list of the
    /// administrator. Passwords are only kept as PBKDF2 hashes and never logged.
    /// </summary>
    public sealed class AuthAppService
    {
        public const string StampClaim = "stamp";
        public const int MinPasswordLength = 8;
        public const int MaxPasswordLength = 256;
        public const int MaxNameLength = 64;
        public static readonly TimeSpan UserCacheTime = TimeSpan.FromSeconds(30);

        private const string WrongLogin = "Name or password is wrong.";

        private readonly ServiceCtrl _service;
        private readonly LoginThrottle _throttle = new();
        private readonly ConcurrentDictionary<int, (AppUserModel User, DateTime ReadAtUtc)> _cache = new();
        private volatile bool _setupDone;

        public AuthAppService(ServiceCtrl service)
        {
            _service = service;
        }

        public bool SetupRequired()
        {
            if (_setupDone)
            {
                return false;
            }

            var required = _service.DataAccess.AppUserCount() == 0;
            _setupDone = !required;
            return required;
        }

        /// <summary>
        /// Creates the administrator while there is no user; throws when a user exists already.
        /// </summary>
        public AppUserModel Setup(LoginDto dto, string address)
        {
            var name = ValidateName(dto?.Name);
            ValidatePassword(dto?.Password);
            var user = new AppUserModel
            {
                Name = name,
                Role = AppUserModel.RoleAdmin,
                PasswordHash = PasswordHashing.Hash(dto.Password),
                SecurityStamp = AppUserModel.NewSecurityStamp()
            };
            if (!_service.DataAccess.AppUserCreate(user, onlyIfNone: true))
            {
                _setupDone = true;
                throw new InvalidOperationException("The administrator was already created. Please sign in.");
            }

            _setupDone = true;
            _service.DataAccess.AppUserTouchLogin(user.Id);
            Log.Information("Auth: first administrator {UserName} created from {Address}", user.Name, address);
            return user;
        }

        /// <summary>
        /// The user for a name and password; throws with the same text for an unknown name and a wrong password.
        /// </summary>
        public AppUserModel Login(LoginDto dto, string address)
        {
            var name = (dto?.Name ?? "").Trim();
            var locked = _throttle.LockedFor(name, address);
            if (locked > TimeSpan.Zero)
            {
                Log.Warning("Auth: login of {UserName} from {Address} refused, locked for {Seconds} s", name, address,
                    (int)Math.Ceiling(locked.TotalSeconds));
                throw new UnauthorizedAccessException(
                    $"Too many attempts, try again in {(int)Math.Ceiling(locked.TotalSeconds)} s.");
            }

            var user = name.Length == 0 ? null : _service.DataAccess.AppUserGetByName(name);
            var ok = user == null
                ? DummyCheck(dto?.Password)
                : PasswordHashing.Verify(dto?.Password ?? "", user.PasswordHash);
            if (!ok)
            {
                _throttle.Failed(name, address);
                Log.Warning("Auth: failed login of {UserName} from {Address}", name, address);
                throw new UnauthorizedAccessException(WrongLogin);
            }

            _throttle.Succeeded(name, address);
            if (PasswordHashing.NeedsRehash(user.PasswordHash))
            {
                user.PasswordHash = PasswordHashing.Hash(dto.Password);
                _service.DataAccess.AppUserUpdate(user);
            }

            _service.DataAccess.AppUserTouchLogin(user.Id);
            Remember(user);
            Log.Information("Auth: {UserName} signed in from {Address}", user.Name, address);
            return user;
        }

        /// <summary>
        /// Changes the password of the signed-in user and gives a new stamp (other sessions end).
        /// </summary>
        public AppUserModel ChangeOwnPassword(int userId, PasswordChangeDto dto)
        {
            var user = _service.DataAccess.AppUserGetById(userId)
                ?? throw new UnauthorizedAccessException("The user does not exist any more.");
            if (!PasswordHashing.Verify(dto?.Current ?? "", user.PasswordHash))
            {
                throw new ArgumentException("The current password is wrong.");
            }

            ValidatePassword(dto?.New);
            user.PasswordHash = PasswordHashing.Hash(dto.New);
            user.SecurityStamp = AppUserModel.NewSecurityStamp();
            _service.DataAccess.AppUserUpdate(user);
            Remember(user);
            Log.Information("Auth: {UserName} changed the password", user.Name);
            return user;
        }

        public List<AppUserDto> GetUsers()
        {
            return _service.DataAccess.AppUserGetAll().Select(ToDto).ToList();
        }

        public AppUserDto CreateUser(AppUserWriteDto dto, string byUser)
        {
            var name = ValidateName(dto?.Name);
            var role = ValidateRole(dto?.Role ?? AppUserModel.RoleViewer);
            ValidatePassword(dto?.Password);
            var user = new AppUserModel
            {
                Name = name,
                Role = role,
                PasswordHash = PasswordHashing.Hash(dto.Password),
                SecurityStamp = AppUserModel.NewSecurityStamp()
            };
            _service.DataAccess.AppUserCreate(user);
            Log.Information("Auth: {ByUser} created user {UserName} ({Role})", byUser, user.Name, user.Role);
            return ToDto(_service.DataAccess.AppUserGetById(user.Id));
        }

        public AppUserDto UpdateUser(int id, AppUserWriteDto dto, string byUser)
        {
            var user = _service.DataAccess.AppUserGetById(id) ?? throw new KeyNotFoundException("The user does not exist.");
            var changed = false;
            if (dto?.Role != null)
            {
                var role = ValidateRole(dto.Role);
                changed |= role != user.Role;
                user.Role = role;
            }

            if (!string.IsNullOrEmpty(dto?.Password))
            {
                ValidatePassword(dto.Password);
                user.PasswordHash = PasswordHashing.Hash(dto.Password);
                changed = true;
            }

            if (changed)
            {
                user.SecurityStamp = AppUserModel.NewSecurityStamp();
                _service.DataAccess.AppUserUpdate(user);
                Forget(id);
                Log.Information("Auth: {ByUser} changed user {UserName} (role {Role}, password {Password})", byUser,
                    user.Name, user.Role, string.IsNullOrEmpty(dto?.Password) ? "kept" : "reset");
            }

            return ToDto(_service.DataAccess.AppUserGetById(id));
        }

        public void DeleteUser(int id, string byUser)
        {
            var user = _service.DataAccess.AppUserGetById(id) ?? throw new KeyNotFoundException("The user does not exist.");
            _service.DataAccess.AppUserDelete(id);
            Forget(id);
            Log.Information("Auth: {ByUser} deleted user {UserName}", byUser, user.Name);
        }

        /// <summary>
        /// The current state of a signed-in user for the cookie check, null when the user was deleted; read at
        /// most every <see cref="UserCacheTime"/>, changes made here take effect at once.
        /// </summary>
        public AppUserModel GetCurrent(int id)
        {
            var now = DateTime.UtcNow;
            if (_cache.TryGetValue(id, out var entry) && now - entry.ReadAtUtc < UserCacheTime)
            {
                return entry.User;
            }

            var user = _service.DataAccess.AppUserGetById(id);
            _cache[id] = (user, now);
            return user;
        }

        public static ClaimsPrincipal ToPrincipal(AppUserModel user, string scheme)
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim(StampClaim, user.SecurityStamp)
            ], scheme, ClaimTypes.Name, ClaimTypes.Role);
            return new ClaimsPrincipal(identity);
        }

        public static AuthUserDto ToAuthUser(ClaimsPrincipal principal)
        {
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            return new AuthUserDto
            {
                Id = UserId(principal) ?? 0,
                Name = principal.Identity.Name ?? "",
                Role = principal.FindFirstValue(ClaimTypes.Role) ?? ""
            };
        }

        public static int? UserId(ClaimsPrincipal principal)
        {
            return int.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
        }

        private void Remember(AppUserModel user)
        {
            _cache[user.Id] = (user, DateTime.UtcNow);
        }

        private void Forget(int id)
        {
            _cache.TryRemove(id, out _);
        }

        private static bool DummyCheck(string password)
        {
            PasswordHashing.VerifyDummy(password);
            return false;
        }

        private static AppUserDto ToDto(AppUserModel user)
        {
            return new AppUserDto
            {
                Id = user.Id,
                Name = user.Name,
                Role = user.Role,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt
            };
        }

        internal static string ValidateName(string name)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
            {
                throw new ArgumentException($"Please enter a name of 1 to {MaxNameLength} characters.");
            }

            return trimmed;
        }

        internal static void ValidatePassword(string password)
        {
            if (password == null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            {
                throw new ArgumentException($"The password needs at least {MinPasswordLength} characters.");
            }
        }

        private static string ValidateRole(string role)
        {
            var value = (role ?? "").Trim().ToLowerInvariant();
            if (!AppUserModel.IsValidRole(value))
            {
                throw new ArgumentException("Please choose the role admin or viewer.");
            }

            return value;
        }
    }
}
