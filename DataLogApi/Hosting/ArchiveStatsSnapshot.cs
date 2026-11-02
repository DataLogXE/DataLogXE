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

using DataLogLib.Services;

namespace DataLogApi.Hosting
{
    public sealed class ArchiveStatsSnapshot
    {
        private readonly object _sync = new();
        private long? _valuesPerSec;
        private double? _valueCountSumMio;
        private double? _databaseDiskSizeGb;
        private DateTime? _statsUpdatedAt;

        public void Attach(IArchiveAccess archive)
        {
            archive.OnValueCountPerSec += (_, e) =>
            {
                lock (_sync)
                {
                    _valuesPerSec = e.Item2;
                    _statsUpdatedAt = e.Item1;
                }
            };

            archive.OnValueCountSumMio += (_, e) =>
            {
                lock (_sync)
                {
                    _valueCountSumMio = e.Item2;
                    _statsUpdatedAt = e.Item1;
                }
            };

            archive.OnDatabaseDiskSizeGb += (_, e) =>
            {
                lock (_sync)
                {
                    _databaseDiskSizeGb = e.Item2;
                    _statsUpdatedAt = e.Item1;
                }
            };
        }

        public SnapshotValue Get()
        {
            lock (_sync)
            {
                return new SnapshotValue(_valuesPerSec, _valueCountSumMio, _databaseDiskSizeGb, _statsUpdatedAt);
            }
        }

        public readonly record struct SnapshotValue(
            long? ValuesPerSec,
            double? ValueCountSumMio,
            double? DatabaseDiskSizeGb,
            DateTime? StatsUpdatedAt);
    }
}
