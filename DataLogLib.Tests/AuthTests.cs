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

using Dapper;
using DataLogLib.Common.Crypt;
using DataLogLib.Models;
using DataLogLib.Services;
using DataLogLib.Services.Auth;
using Npgsql;
using Xunit;

namespace DataLogLib.Tests
{
    public class PasswordHashingTests
    {
        [Fact]
        public void A_hash_verifies_its_password_only()
        {
            var hash = PasswordHashing.Hash("correct horse", 2000);

            Assert.StartsWith("pbkdf2-sha256$2000$", hash);
            Assert.True(PasswordHashing.Verify("correct horse", hash));
            Assert.False(PasswordHashing.Verify("correct hors", hash));
            Assert.False(PasswordHashing.Verify("", hash));
            Assert.False(PasswordHashing.Verify(null, hash));
        }

        [Fact]
        public void The_same_password_gets_another_salt_each_time()
        {
            Assert.NotEqual(PasswordHashing.Hash("same", 2000), PasswordHashing.Hash("same", 2000));
        }

        [Fact]
        public void Broken_or_foreign_values_never_verify()
        {
            var hash = PasswordHashing.Hash("secret", 2000);
            var parts = hash.Split('$');
            var bytes = Convert.FromBase64String(parts[3]);
            bytes[0] ^= 1;
            var changed = string.Join('$', parts[0], parts[1], parts[2], Convert.ToBase64String(bytes));

            Assert.False(PasswordHashing.Verify("secret", changed));
            Assert.False(PasswordHashing.Verify("secret", ""));
            Assert.False(PasswordHashing.Verify("secret", null));
            Assert.False(PasswordHashing.Verify("secret", "$2a$14$bcryptvaluefromcaddy"));
            Assert.False(PasswordHashing.Verify("secret", "pbkdf2-sha256$12$AAAA$AAAA"));
        }

        [Fact]
        public void Fewer_iterations_than_the_default_ask_for_a_new_hash()
        {
            Assert.True(PasswordHashing.NeedsRehash(PasswordHashing.Hash("x", 2000)));
            Assert.False(PasswordHashing.NeedsRehash(PasswordHashing.Hash("x")));
            Assert.True(PasswordHashing.NeedsRehash("garbage"));
        }
    }

    public class LoginThrottleTests
    {
        private DateTime _now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        private LoginThrottle Create() => new(() => _now);

        [Fact]
        public void Five_failures_lock_the_name_for_a_minute()
        {
            var throttle = Create();
            for (var i = 0; i < LoginThrottle.FreeAttempts - 1; i++)
            {
                throttle.Failed("admin", "10.0.0.1");
                Assert.Equal(TimeSpan.Zero, throttle.LockedFor("admin", "10.0.0.1"));
            }

            throttle.Failed("admin", "10.0.0.1");
            Assert.Equal(LoginThrottle.FirstLock, throttle.LockedFor("ADMIN ", "10.0.0.2"));
            Assert.Equal(LoginThrottle.FirstLock, throttle.LockedFor("other", "10.0.0.1"));
            Assert.Equal(TimeSpan.Zero, throttle.LockedFor("other", "10.0.0.2"));

            _now += LoginThrottle.FirstLock;
            Assert.Equal(TimeSpan.Zero, throttle.LockedFor("admin", "10.0.0.1"));
        }

        [Fact]
        public void Every_further_failure_doubles_the_lock_up_to_the_maximum()
        {
            var throttle = Create();
            for (var i = 0; i < LoginThrottle.FreeAttempts; i++)
            {
                throttle.Failed("admin", "a");
            }

            throttle.Failed("admin", "a");
            Assert.Equal(TimeSpan.FromMinutes(2), throttle.LockedFor("admin", "a"));
            for (var i = 0; i < 10; i++)
            {
                throttle.Failed("admin", "a");
            }

            Assert.Equal(LoginThrottle.MaxLock, throttle.LockedFor("admin", "a"));
        }

        [Fact]
        public void A_success_clears_name_and_address()
        {
            var throttle = Create();
            for (var i = 0; i < LoginThrottle.FreeAttempts; i++)
            {
                throttle.Failed("admin", "a");
            }

            throttle.Succeeded("admin", "a");
            Assert.Equal(TimeSpan.Zero, throttle.LockedFor("admin", "a"));
            Assert.Equal(0, throttle.Count);
        }

        [Fact]
        public void The_number_of_kept_keys_is_bounded()
        {
            var throttle = Create();
            for (var i = 0; i < LoginThrottle.MaxEntries; i++)
            {
                _now = _now.AddMilliseconds(1);
                throttle.Failed("user" + i, "same");
            }

            Assert.True(throttle.Count <= LoginThrottle.MaxEntries);
        }
    }

    [Collection(PostgresTestConfiguration.Collection)]
    public class AppUserStoreTests : IDisposable
    {
        private readonly DataAccessCtrl _access;

        public AppUserStoreTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            _access = new DataAccessCtrl();
            _access.Init(PostgresTestConfiguration.Create());
            Clear();
        }

        public void Dispose()
        {
            if (_access == null)
            {
                return;
            }

            Clear();
            _access.Close();
        }

        private static void Clear()
        {
            using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
            using var connection = dataSource.OpenConnection();
            connection.Execute("DELETE FROM appuser");
        }

        private static AppUserModel User(string name, string role) => new()
        {
            Name = name,
            Role = role,
            PasswordHash = PasswordHashing.Hash("password1", 2000),
            SecurityStamp = AppUserModel.NewSecurityStamp()
        };

        [Fact]
        public void The_setup_creates_only_the_first_user()
        {
            if (_access == null)
            {
                return;
            }

            Assert.Equal(0, _access.AppUserCount());
            Assert.True(_access.AppUserCreate(User("admin", AppUserModel.RoleAdmin), onlyIfNone: true));
            Assert.False(_access.AppUserCreate(User("second", AppUserModel.RoleAdmin), onlyIfNone: true));
            Assert.Equal(1, _access.AppUserCount());
        }

        [Fact]
        public void Names_are_unique_without_regard_to_case_and_found_that_way()
        {
            if (_access == null)
            {
                return;
            }

            _access.AppUserCreate(User("Alice", AppUserModel.RoleAdmin));
            var ex = Assert.Throws<InvalidOperationException>(() => _access.AppUserCreate(User("alice", AppUserModel.RoleViewer)));
            Assert.Contains("already exists", ex.Message);

            var found = _access.AppUserGetByName("ALICE");
            Assert.Equal("Alice", found.Name);
            Assert.True(found.IsAdmin);
            Assert.Null(found.LastLoginAt);

            _access.AppUserTouchLogin(found.Id);
            Assert.NotNull(_access.AppUserGetById(found.Id).LastLoginAt);
            Assert.Null(_access.AppUserGetByName("nobody"));
        }

        [Fact]
        public void The_last_administrator_cannot_be_deleted_or_made_a_viewer()
        {
            if (_access == null)
            {
                return;
            }

            var admin = User("admin", AppUserModel.RoleAdmin);
            var viewer = User("viewer", AppUserModel.RoleViewer);
            _access.AppUserCreate(admin);
            _access.AppUserCreate(viewer);

            admin.Role = AppUserModel.RoleViewer;
            Assert.Throws<InvalidOperationException>(() => _access.AppUserUpdate(admin));
            Assert.Throws<InvalidOperationException>(() => _access.AppUserDelete(admin.Id));
            Assert.True(_access.AppUserGetById(admin.Id).IsAdmin);

            // With a second administrator both work
            viewer.Role = AppUserModel.RoleAdmin;
            _access.AppUserUpdate(viewer);
            admin.Role = AppUserModel.RoleViewer;
            _access.AppUserUpdate(admin);
            _access.AppUserDelete(admin.Id);
            Assert.Equal(1, _access.AppUserCount());
            Assert.Throws<KeyNotFoundException>(() => _access.AppUserDelete(admin.Id));
        }

        [Fact]
        public void The_reset_sets_a_new_password_and_stamp_or_creates_an_administrator()
        {
            if (_access == null)
            {
                return;
            }

            var viewer = User("viewer", AppUserModel.RoleViewer);
            _access.AppUserCreate(viewer);

            Assert.False(AppUserReset.Reset(TestPostgres.ConnectionString, "VIEWER", "new-password"));
            var reset = _access.AppUserGetById(viewer.Id);
            Assert.True(PasswordHashing.Verify("new-password", reset.PasswordHash));
            Assert.NotEqual(viewer.SecurityStamp, reset.SecurityStamp);
            Assert.Equal(AppUserModel.RoleViewer, reset.Role);

            Assert.True(AppUserReset.Reset(TestPostgres.ConnectionString, "rescue", "rescue-password"));
            var rescue = _access.AppUserGetByName("rescue");
            Assert.True(rescue.IsAdmin);
            Assert.True(PasswordHashing.Verify("rescue-password", rescue.PasswordHash));
        }
    }
}
