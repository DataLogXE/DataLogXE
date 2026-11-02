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

namespace DataLogLib.Services.Auth
{
    /// <summary>
    /// Slows down guessing: per user name and per client address, 5 failed logins in a row lock further tries for
    /// 1 min, every further failure doubles the lock up to 15 min; a successful login clears the name and the
    /// address. Kept in memory, at most <see cref="MaxEntries"/> keys (the oldest are dropped).
    /// </summary>
    public sealed class LoginThrottle
    {
        public const int FreeAttempts = 5;
        public const int MaxEntries = 2000;
        public static readonly TimeSpan FirstLock = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);

        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Func<DateTime> _now;

        public LoginThrottle(Func<DateTime> now = null)
        {
            _now = now ?? (() => DateTime.UtcNow);
        }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>
        /// How long the name or the address is still locked, zero when a try is allowed.
        /// </summary>
        public TimeSpan LockedFor(string name, string address)
        {
            var now = _now();
            lock (_sync)
            {
                var a = Remaining(NameKey(name), now);
                var b = Remaining(AddressKey(address), now);
                return a > b ? a : b;
            }
        }

        public void Failed(string name, string address)
        {
            var now = _now();
            lock (_sync)
            {
                Fail(NameKey(name), now);
                Fail(AddressKey(address), now);
            }
        }

        public void Succeeded(string name, string address)
        {
            lock (_sync)
            {
                _entries.Remove(NameKey(name));
                _entries.Remove(AddressKey(address));
            }
        }

        private TimeSpan Remaining(string key, DateTime now)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.LockedUntil <= now)
            {
                return TimeSpan.Zero;
            }

            return entry.LockedUntil - now;
        }

        private void Fail(string key, DateTime now)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count >= MaxEntries)
                {
                    var oldest = _entries.MinBy(e => e.Value.LastFailure).Key;
                    _entries.Remove(oldest);
                }

                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Failures++;
            entry.LastFailure = now;
            if (entry.Failures >= FreeAttempts)
            {
                var doublings = Math.Min(entry.Failures - FreeAttempts, 10);
                var lockTime = TimeSpan.FromTicks(Math.Min(FirstLock.Ticks << doublings, MaxLock.Ticks));
                entry.LockedUntil = now + lockTime;
            }
        }

        private static string NameKey(string name) => "n:" + (name ?? "").Trim().ToLowerInvariant();

        private static string AddressKey(string address) => "a:" + (address ?? "");

        private sealed class Entry
        {
            public int Failures;
            public DateTime LastFailure;
            public DateTime LockedUntil;
        }
    }
}
