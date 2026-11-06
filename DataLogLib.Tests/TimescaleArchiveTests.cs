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

using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services;
using DataLogLib.Services.ArchiveAccess;
using DataLogLib.Services.EventLog;
using Npgsql;
using Xunit;

namespace DataLogLib.Tests
{
    [Collection(PostgresTestConfiguration.Collection)]
    public class TimescaleArchiveTests
    {
        private sealed class UidLookup : ITagUidLookup
        {
            public IReadOnlyDictionary<int, string> DataTagUidsGet(IReadOnlyCollection<int> dataTagIds)
                => dataTagIds.Where(id => id > 0).Distinct().ToDictionary(id => id, Uid);
        }

        private const long NinetyDaysSeconds = 90L * 24 * 3600;

        private readonly TimescaleArchive _archive;
        // The hold time of the newest value of a tag, as of a tag with a short cycle
        private readonly TimeSpan _hold = TimeSpan.FromSeconds(30);
        // A local full hour a few hours back, so the values lie in closed chunks
        private readonly DateTime _base;

        public TimescaleArchiveTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            PostgresTestConfiguration.EnsureBaselineApplied();
            foreach (var table in TimescaleArchive.RetentionTables.Append(AppEventStore.TableName))
            {
                Execute($"TRUNCATE {table}");
            }
            Execute("TRUNCATE archivetag RESTART IDENTITY");
            Execute("DELETE FROM parameter WHERE key = 'COMPRESSION_VERIFIED_UNTIL'");
            // An empty counted range at the current minute, as after the upgrade to schema 12
            Execute($"UPDATE parameter SET value = (floor(extract(epoch FROM now()) / 60) * 60)::bigint::text "
                + $"WHERE key IN ('{TimescaleArchive.CountFromKey}', '{TimescaleArchive.CountUntilKey}')");

            _archive = new TimescaleArchive();
            _archive.Init(PostgresTestConfiguration.Create());
            _archive.SetUidLookup(new UidLookup());
            var start = DateTime.Now.AddHours(-3);
            _base = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Local);
        }

        [Fact]
        public void Raw_values_round_trip_in_time_order_with_nan_and_invalid_states()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([Value(1, 2.5, 3), Value(1, 1.5, 1), Value(1, double.NaN, 2, ValueState.Invalid), Value(1, 4.25, 5)]);

            var raw = ReadRaw(1, _base, _base.AddMinutes(1));
            Assert.Equal([1.0, 2, 3, 5], raw.Select(v => (v.Time - _base).TotalSeconds));
            Assert.True(double.IsNaN(raw[1].Value));
            Assert.Equal(ValueState.Invalid, raw[1].State);
            Assert.Equal(4.25, raw[3].Value);
            Assert.Equal(4, _archive.GetRawValueCount(1, _base, _base.AddMinutes(1)));
            Assert.Equal(2, _archive.GetRawValueCount(1, _base.AddSeconds(2), _base.AddSeconds(5)));

            // The invalid state leaves a gap point, the range end is exclusive
            var points = _archive.GetPlotPoints(1, CompressionType.RAW, _base, _base.AddSeconds(5));
            Assert.Equal([1.5, null, 2.5], points.Select(p => p.Raw));
            Assert.Equal(_base.AddSeconds(1).ToUnixSeconds(), points[0].X);
        }

        [Fact]
        public void Real_values_are_read_with_their_shortest_digits()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([Value(1, 100.62626, 1)]);

            var point = Assert.Single(_archive.GetPlotPoints(1, CompressionType.RAW, _base, _base.AddMinutes(1)));
            Assert.Equal(100.62626, point.Raw);
        }

        [Fact]
        public void Raw_M4_points_are_the_first_min_max_and_last_value_per_bucket_and_the_gaps()
        {
            if (_archive == null)
            {
                return;
            }

            // Bucket 0 (0 s up to 1 s): five values; bucket 1: one value and an invalid state; bucket 2: two invalid
            // states around a value. The times are exact in binary, so they keep their milliseconds.
            _archive.InsertValues([
                Value(1, 5, 0.125), Value(1, 1, 0.25), Value(1, 9, 0.375), Value(1, 3, 0.5), Value(1, 4, 0.625),
                Value(1, 7, 1.25), Value(1, double.NaN, 1.75, ValueState.Invalid),
                Value(1, double.NaN, 2.125, ValueState.Invalid), Value(1, 6, 2.5), Value(1, double.NaN, 2.875, ValueState.Invalid)
            ]);

            var points = _archive.GetRawM4PlotPoints(1, _base, _base.AddSeconds(3), _base.ToUnixSeconds(), 1);
            Assert.Equal(
                [0.125, 0.25, 0.375, 0.625, 1.25, 1.75, 2.125, 2.5, 2.875],
                points.Select(p => Math.Round(p.X - _base.ToUnixSeconds(), 3)));
            Assert.Equal([5.0, 1, 9, 4, 7, null, null, 6, null], points.Select(p => p.Raw));

            // The range end is exclusive and an empty bucket width reads nothing
            Assert.Equal(4, _archive.GetRawM4PlotPoints(1, _base, _base.AddSeconds(1), _base.ToUnixSeconds(), 1).Count);
            Assert.Empty(_archive.GetRawM4PlotPoints(1, _base, _base.AddSeconds(3), _base.ToUnixSeconds(), 0));
        }

        [Fact]
        public void Raw_values_for_an_export_stop_at_the_limit()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([Value(1, 1, 1), Value(1, double.NaN, 2, ValueState.Invalid), Value(1, 3, 3), Value(1, 4, 4)]);

            // In time order with the invalid state as a gap, at most limit rows, nothing for no limit
            Assert.Equal([1.0, null, 3, 4], _archive.GetRawPlotPoints(1, _base, _base.AddMinutes(1), 10).Select(p => p.Raw));
            Assert.Equal([1.0, null], _archive.GetRawPlotPoints(1, _base, _base.AddMinutes(1), 2).Select(p => p.Raw));
            Assert.Empty(_archive.GetRawPlotPoints(1, _base, _base.AddMinutes(1), 0));
        }

        [Fact]
        public void Raw_values_for_an_export_report_the_rows_read()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues(Enumerable.Range(0, 25_000).Select(i => Value(1, i, i * 0.01)).ToList());

            // Every 10 000 rows and once at the end, so the export can show how far a long read got
            List<long> reports = [];
            var points = _archive.GetRawPlotPoints(1, _base, _base.AddMinutes(5), 100_000, default, reports.Add);
            Assert.Equal(25_000, points.Count);
            Assert.Equal([10_000L, 20_000, 25_000], reports);
        }

        [Fact]
        public async Task Streamed_raw_values_match_the_read_ones()
        {
            if (_archive == null)
            {
                return;
            }

            // An invalid state clears the values within a millisecond on both sides, also those read before it (the
            // times are milliseconds and a half, which the archive cuts to the millisecond)
            _archive.InsertValues([
                Value(1, 1, 1), Value(1, 2, 1.9995), Value(1, double.NaN, 2.0005, ValueState.Invalid), Value(1, 3, 2.0015),
                Value(1, 4, 2.0025), Value(1, 5, 3), Value(1, 6, 3.0045), Value(1, double.NaN, 3.0055, ValueState.Invalid),
                Value(1, 7, 4)
            ]);

            var read = _archive.GetRawPlotPoints(1, _base, _base.AddMinutes(1), 100);
            List<PlotPoint> streamed = [];
            await foreach (var point in _archive.StreamRawPlotPoints(1, _base, _base.AddMinutes(1)))
            {
                streamed.Add(point);
            }
            Assert.Equal([1.0, null, null, null, 4, 5, null, null, 7], streamed.Select(p => p.Raw));
            Assert.Equal(read.Select(p => (p.X, p.Raw)), streamed.Select(p => (p.X, p.Raw)));
        }

        [Fact]
        public void Compressed_levels_keep_one_row_per_time()
        {
            if (_archive == null)
            {
                return;
            }

            List<ArchiveValue> c001 = [Value(2, 1, 0, compression: CompressionType.C001), Value(2, 2, 1, compression: CompressionType.C001)];
            _archive.InsertValuesAndWait(c001);
            _archive.InsertValuesAndWait(c001);
            _archive.InsertCompressedValuesAndWait(Uid(2), c001);

            Assert.Equal("2", Scalar("SELECT count(*) FROM archivec001"));
            Assert.Equal(2, _archive.GetPlotPoints(2, CompressionType.C001, _base, _base.AddMinutes(1)).Count);
            Assert.Empty(_archive.GetPlotPoints(2, CompressionType.RAW, _base, _base.AddMinutes(1)));
        }

        [Fact]
        public void Values_are_read_from_and_written_into_compressed_chunks()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([Value(3, 1, 0), Value(3, 2, 60)]);
            _archive.InsertValuesAndWait([Value(3, 1, 0, compression: CompressionType.C001)]);
            Execute("SELECT compress_chunk(c, if_not_compressed => true) FROM show_chunks('archiveraw') c");
            Execute("SELECT compress_chunk(c, if_not_compressed => true) FROM show_chunks('archivec001') c");
            Assert.Equal("True", Scalar(
                "SELECT bool_and(is_compressed) FROM timescaledb_information.chunks WHERE hypertable_name = 'archiveraw'"));

            Assert.Equal([1.0, 2], ReadRaw(3, _base, _base.AddMinutes(5)).Select(v => v.Value));

            _archive.InsertValues([Value(3, 3, 30)]);
            _archive.InsertValuesAndWait([Value(3, 1, 0, compression: CompressionType.C001), Value(3, 5, 30, compression: CompressionType.C001)]);

            Assert.Equal([1.0, 3, 2], ReadRaw(3, _base, _base.AddMinutes(5)).Select(v => v.Value));
            Assert.Equal([1.0, 5], _archive.GetPlotPoints(3, CompressionType.C001, _base, _base.AddMinutes(5)).Select(p => p.Raw));
        }

        [Fact]
        public void A_level_write_with_a_value_of_days_ago_skips_exactly_the_existing_rows()
        {
            if (_archive == null)
            {
                return;
            }

            // Tag 1 has a point 3 days back, tags 2 and 3 in the current hour, all in compressed chunks
            const double daysBack = -3 * 86400;
            _archive.InsertValuesAndWait([
                Value(1, 1, daysBack, compression: CompressionType.C001),
                Value(2, 2, 0, compression: CompressionType.C001),
                Value(3, 3, 0, compression: CompressionType.C001)]);
            Execute("SELECT compress_chunk(c, if_not_compressed => true) FROM show_chunks('archivec001') c");

            // One batch as after a source start: the existing points again, a point 5 s after the old one (like
            // the startup value of a tag without new values) and new current points, one of them at the time of
            // an existing point of another tag
            _archive.InsertValuesAndWait([
                Value(1, 1, daysBack, compression: CompressionType.C001),
                Value(1, 4, daysBack + 5, compression: CompressionType.C001),
                Value(2, 2, 0, compression: CompressionType.C001),
                Value(2, 5, 1, compression: CompressionType.C001),
                Value(4, 6, 0, compression: CompressionType.C001)]);

            Assert.Equal("6", Scalar("SELECT count(*) FROM archivec001"));
            var old = _base.AddSeconds(daysBack);
            Assert.Equal([1.0, 4], _archive.GetPlotPoints(1, CompressionType.C001, old, old.AddMinutes(1)).Select(p => p.Raw));
            Assert.Equal([2.0, 5], _archive.GetPlotPoints(2, CompressionType.C001, _base, _base.AddMinutes(1)).Select(p => p.Raw));
            Assert.Equal([6.0], _archive.GetPlotPoints(4, CompressionType.C001, _base, _base.AddMinutes(1)).Select(p => p.Raw));
        }

        [Fact]
        public void Hour_coverage_compares_the_raw_hour_with_the_coarsest_level()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([
                Value(4, 1, 10), Value(4, 2, 20 * 60), Value(4, 3, 50 * 60),
                Value(4, 4, 65 * 60), Value(4, 5, 90 * 60)]);
            _archive.InsertValuesAndWait([
                Value(4, 1, 10, compression: CompressionType.C3600),
                Value(4, 3, 50 * 60, compression: CompressionType.C3600),
                Value(4, 4, 65 * 60, compression: CompressionType.C3600)]);

            var hours = _archive.GetHourCoverage(Uid(4), _base, _base.AddHours(2));

            Assert.Equal(2, hours.Count);
            Assert.Equal(_base, hours[0].Hour);
            Assert.Equal(_base.AddSeconds(10), hours[0].RawFirst);
            Assert.Equal(_base.AddMinutes(50), hours[0].RawLast);
            Assert.Equal(3, hours[0].RawCount);
            Assert.True(hours[0].IsComplete);
            Assert.Equal(_base.AddHours(1), hours[1].Hour);
            Assert.Equal(_base.AddMinutes(65), hours[1].LevelLast);
            Assert.False(hours[1].IsComplete);
        }

        [Fact]
        public void Repair_finds_the_first_raw_value_and_the_last_value_of_every_level()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([Value(5, 1, 10), Value(5, 2, 40), Value(6, 3, 20)]);
            _archive.InsertValuesAndWait([Value(5, 1, 10, compression: CompressionType.C010)]);

            var first = _archive.GetRawUids(_base, _base.AddMinutes(1)).OrderBy(u => u.Uid).ToList();
            Assert.Equal([(Uid(5), _base.AddSeconds(10)), (Uid(6), _base.AddSeconds(20))], first);

            var last = _archive.GetLastTimes(_base, _base.AddMinutes(1));
            Assert.Contains((Uid(5), CompressionType.RAW, _base.AddSeconds(40)), last);
            Assert.Contains((Uid(5), CompressionType.C010, _base.AddSeconds(10)), last);
            Assert.Equal(3, last.Count);

            Assert.True(_archive.TryGetUid(5, out var uid));
            Assert.Equal(Uid(5), uid);
        }

        [Fact]
        public void Verified_time_and_value_counts_are_kept()
        {
            if (_archive == null)
            {
                return;
            }

            Assert.Null(_archive.GetCompressionVerifiedUntil());
            _archive.SetCompressionVerifiedUntil(_base);
            Assert.Equal(_base, _archive.GetCompressionVerifiedUntil());

            Assert.Equal(0, _archive.GetValueCountSum());
            _archive.InsertValueCount(_base, 5);
            _archive.InsertValueCount(_base, 7);
            _archive.InsertValueCount(_base.AddMinutes(1), 3);
            Assert.Equal(15, _archive.GetValueCountSum());
        }

        [Fact]
        public void Start_end_and_size_of_the_archive()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([Value(1, 1, 5), Value(1, 2, 15), Value(7, 3, 25)]);

            Assert.Equal(_base.AddSeconds(5), _archive.GetArchiveStartTime());
            Assert.Equal(_base.AddSeconds(15), _archive.GetArchiveEndTime(1));
            Assert.Equal(DateTime.MinValue, _archive.GetArchiveEndTime(99));
            Assert.Equal(2, _archive.GetPlotPointAt(1, _base.AddSeconds(20)).Raw);
            Assert.True(_archive.GetDatabaseDiskSizeBytes() > 0);
        }

        [Fact]
        public void Raw_value_at_a_time_is_the_last_stored_value_not_a_plot_point()
        {
            if (_archive == null)
            {
                return;
            }

            _archive.InsertValues([Value(1, 100.89722, 10), Value(1, 7.5, 20), Value(1, double.NaN, 30, ValueState.Invalid), Value(2, 3, 5)]);

            // Now is 40 s after the base: none of the times below lies in the future
            _archive.UtcNow = () => _base.AddSeconds(40).ToUniversalTime();
            var lookback = TimeSpan.FromDays(30);
            Assert.Null(_archive.GetRawValueAt(1, _base.AddSeconds(9), lookback, _hold));
            Assert.Equal(100.89722, _archive.GetRawValueAt(1, _base.AddSeconds(10), lookback, _hold).Raw);
            var between = _archive.GetRawValueAt(1, _base.AddSeconds(15.5), lookback, _hold);
            Assert.Equal(100.89722, between.Raw);
            Assert.Equal(_base.AddSeconds(10).ToUnixSeconds(), between.X);
            // A value before an invalid one is lost: it has its time but no value, like the gap of a chart there
            var lost = _archive.GetRawValueAt(1, _base.AddSeconds(25), lookback, _hold);
            Assert.Null(lost.Raw);
            Assert.Equal(_base.AddSeconds(20).ToUnixSeconds(), lost.X);
            // An invalid value has a time but no value
            var invalid = _archive.GetRawValueAt(1, _base.AddSeconds(40), lookback, _hold);
            Assert.Null(invalid.Raw);
            Assert.Equal(_base.AddSeconds(30).ToUnixSeconds(), invalid.X);
            // A lookback that ends after the value, and a tag without values
            Assert.Null(_archive.GetRawValueAt(1, _base.AddSeconds(40), TimeSpan.FromSeconds(5), _hold));
            Assert.Null(_archive.GetRawValueAt(99, _base.AddSeconds(40), lookback, _hold));
            // Another tag: its only value is the newest, so it holds for the hold time (30 s) from its row at 5
            _archive.UtcNow = () => _base.AddSeconds(25).ToUniversalTime();
            Assert.Equal(3, _archive.GetRawValueAt(2, _base.AddSeconds(25), lookback, _hold).Raw);
        }

        [Fact]
        public void Raw_value_holds_if_a_valid_value_follows_and_the_newest_one_only_for_the_hold_time()
        {
            if (_archive == null)
            {
                return;
            }

            var lookback = TimeSpan.FromDays(30);
            _archive.InsertValues([
                // Valid values, the last at 30 is the newest
                Value(20, 1, 10), Value(20, 2, 20), Value(20, 3, 30),
                // A source that was lost: nothing for 50 s, then an invalid row at its return and valid values again
                Value(21, 5, 10), Value(21, 6, 20), Value(21, double.NaN, 70, ValueState.Invalid), Value(21, 7, 71)]);
            DateTime Now(double seconds) => _base.AddSeconds(seconds);
            double? At(int tag, double seconds, double now)
            {
                _archive.UtcNow = () => Now(now).ToUniversalTime();
                return _archive.GetRawValueAt(tag, Now(seconds), lookback, _hold)?.Raw;
            }

            // A value holds until the next one: valid ones follow
            Assert.Equal(1, At(20, 15, now: 100));
            Assert.Equal(2, At(20, 25, now: 100));
            // The newest value (30) holds for 30 s from its time: not after that, and not in the future
            Assert.Equal(3, At(20, 45, now: 45));
            Assert.Equal(3, At(20, 60, now: 100));
            Assert.Null(At(20, 61, now: 100));
            Assert.Null(At(20, 100, now: 100));
            Assert.Null(At(20, 46, now: 45));

            // The lost source: the value at 20 is followed by the invalid row at 70, so it is lost from then on, also in
            // the middle of the gap and before the invalid row is stored
            Assert.Equal(5, At(21, 15, now: 100));
            Assert.Null(At(21, 25, now: 100));
            Assert.Null(At(21, 69, now: 100));
            Assert.Null(At(21, 70, now: 100));
            // ... and the value after it holds again, the newest one for 30 s
            Assert.Equal(7, At(21, 75, now: 100));
            Assert.Equal(7, At(21, 100, now: 150));
            Assert.Null(At(21, 102, now: 150));

            // A tag with a long cycle holds its newest value for twice its cycle
            _archive.UtcNow = () => Now(120).ToUniversalTime();
            Assert.Null(_archive.GetRawValueAt(20, Now(110), lookback, _hold)?.Raw);
            Assert.Equal(3, _archive.GetRawValueAt(20, Now(110), lookback, DataLogLib.Common.ArchiveHold.For(60_000))?.Raw);
        }

        [Fact]
        public void The_estimate_of_a_range_reads_the_counters_and_scales_their_part_to_the_range()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            // Nothing counted yet: unknown, not a count of the raw values
            SetCountRange(_base.AddHours(-1), _base.AddHours(-1));
            Assert.Null(_archive.EstimateRawValueCount(1, _base, _base.AddSeconds(9000)));

            Assert.True(_archive.RollUpCounts(_base.AddSeconds(9600).ToUniversalTime()));
            // Counted throughout: the count itself
            Assert.Equal(RawCount(1, _base, _base.AddSeconds(9000)), _archive.EstimateRawValueCount(1, _base, _base.AddSeconds(9000)));
            // Counted for the first half only: twice the count of that half
            var half = RawCount(1, _base.AddSeconds(4800), _base.AddSeconds(9600));
            var estimate = _archive.EstimateRawValueCount(1, _base.AddSeconds(4800), _base.AddSeconds(14400));
            Assert.Equal(2 * half, estimate);
            // A tag without values in the counted range
            Assert.Equal(0, _archive.EstimateRawValueCount(99, _base, _base.AddSeconds(9000)));
        }

        [Fact]
        public void Raw_value_at_a_time_reads_only_the_chunks_next_to_it_and_the_counters()
        {
            if (_archive == null)
            {
                return;
            }

            const double Hour = 3600;
            const double Day = 24 * Hour;
            List<ArchiveValue> values = [];
            // Tag 32 fills a chunk of archiveraw every 30 minutes over 12 days; tag 30 has values from 8 to 5 days back,
            // then none until an hour back; tag 31 has none
            for (var seconds = -12 * Day; seconds <= 0; seconds += Hour / 2)
            {
                values.Add(Value(32, 1, seconds));
                if (seconds >= -8 * Day && seconds <= -5 * Day)
                {
                    values.Add(Value(30, seconds, seconds));
                }
            }
            values.Add(Value(30, 99, -Hour));
            // A lost signal 6 days back, for the statistics of a range around it
            values.Add(Value(30, double.NaN, -6 * Day + 60, ValueState.Invalid));
            _archive.InsertValues(values);
            // The counters hold the raw values from 13 days back on, as after the backfill
            Execute($"INSERT INTO {TimescaleArchive.HourCountTable} (time, tag_id, count) "
                + $"SELECT time_bucket('1 hour', time), tag_id, count(*) FROM {TimescaleArchive.RawTable} GROUP BY 1, 2");
            Execute($"UPDATE parameter SET value = (floor(extract(epoch FROM now() - interval '13 days') / 3600) * 3600)::bigint::text "
                + $"WHERE key = '{TimescaleArchive.CountFromKey}'");

            List<(string Sql, int Chunks)> reads = [];
            _archive.ReadObserver = command =>
            {
                using var explain = new NpgsqlCommand("EXPLAIN " + command.CommandText, command.Connection);
                foreach (NpgsqlParameter parameter in command.Parameters)
                {
                    explain.Parameters.Add(parameter.Clone());
                }
                using var reader = explain.ExecuteReader();
                HashSet<string> chunks = [];
                while (reader.Read())
                {
                    foreach (System.Text.RegularExpressions.Match match in
                        System.Text.RegularExpressions.Regex.Matches(reader.GetString(0), @"_hyper_\d+_\d+_chunk"))
                    {
                        chunks.Add(match.Value);
                    }
                }
                reads.Add((command.CommandText, chunks.Count));
            };
            var lookback = TimeSpan.FromDays(30);
            var at = _base.AddDays(-3);
            void ReadAndCheck(string counters)
            {
                reads.Clear();
                // The value 5 days back holds: the next one stored, 3 days later, is valid
                var point = _archive.GetRawValueAt(30, at, lookback, _hold);
                Assert.Equal(-5 * Day, point.Raw);
                Assert.Equal(_base.AddDays(-5).ToUnixSeconds(), point.X);
                Assert.Null(_archive.GetRawValueAt(31, at, lookback, _hold));
                // The newest value holds for the hold time only
                Assert.Null(_archive.GetRawValueAt(30, _base.AddMinutes(-30), lookback, _hold).Raw);
                Assert.Equal(99, _archive.GetRawValueAt(30, _base.AddSeconds(-Hour + 10), lookback, _hold).Raw);

                // The statistics of 4 hours around the lost signal, and of a range of a tag without values: the rows
                // before and after the range are searched like the raw value at a time
                var (from, to) = (_base.AddDays(-6).AddHours(-2), _base.AddDays(-6).AddHours(2));
                Assert.Equal(8, _archive.GetRawStatistics(30, from, to, lookback, _hold).Count);
                // The row before the invalid one (60 s) and the invalid one until the next row (29 min)
                Assert.Equal(30 * 60, _archive.GetInvalidSeconds(30, from, to, lookback, _hold));
                Assert.Equal(30 * 60, _archive.GetBoolDurations(30, from, to, lookback, _hold).InvalidSeconds);
                Assert.Equal(0, _archive.GetRawStatistics(31, from, to, lookback, _hold).Count);

                // A day of chunks (48) and the edges at most, of 577; the old read planned all chunks after the lookback
                // start
                Assert.NotEmpty(reads);
                var widest = reads.MaxBy(r => r.Chunks);
                Assert.True(widest.Chunks <= 50, $"{counters}: {widest.Chunks} chunks planned by {widest.Sql}");
            }
            try
            {
                ReadAndCheck("with counters");
                // Before the backfill the counters hold nothing: the raw values are searched a day at a time
                Execute($"UPDATE parameter SET value = (SELECT value FROM parameter WHERE key = '{TimescaleArchive.CountUntilKey}') "
                    + $"WHERE key = '{TimescaleArchive.CountFromKey}'");
                ReadAndCheck("without counters");
            }
            finally
            {
                _archive.ReadObserver = null;
            }
        }

        [Fact]
        public void Raw_statistics_describe_the_signal_with_the_value_before_the_range_and_weighted_by_time()
        {
            if (_archive == null)
            {
                return;
            }

            var lookback = TimeSpan.FromDays(30);
            _archive.InsertValues([
                Value(1, 1, 5), Value(1, 2, 10), Value(1, double.NaN, 12, ValueState.Invalid), Value(1, 4, 20),
                Value(1, 8, 30), Value(1, 100, 40), Value(2, 50, 15),
                // The value of the first segment is the one before the range
                Value(12, 97.78, 0.404), Value(12, 99.98, 0.515), Value(12, 102.1, 0.621),
                Value(5, 3, 5), Value(5, double.NaN, 20, ValueState.Invalid), Value(5, 4, 30), Value(5, double.NaN, 50, ValueState.Invalid)]);

            // [10, 40): the value at 10 is lost before the invalid one at 12, which holds until 20; 4 holds 20-30 and 8 holds
            // 30-40 (the value at 40 is outside). Three valid values are stored in the range.
            var stats = _archive.GetRawStatistics(1, _base.AddSeconds(10), _base.AddSeconds(40), lookback, _hold);
            Assert.Equal(3, stats.Count);
            Assert.Equal(4, stats.Min);
            Assert.Equal(8, stats.Max);
            Assert.Equal(6, stats.Mean.Value, 9);
            Assert.Equal(2, stats.StdDev.Value, 9);

            // A single value in the range, held until the end: the deviation of a constant signal is 0
            var single = _archive.GetRawStatistics(2, _base, _base.AddMinutes(1), lookback, _hold);
            Assert.Equal(1, single.Count);
            Assert.Equal(50, single.Mean);
            Assert.Equal(0, single.StdDev);

            // No value in the range: the one before it holds through it
            var empty = _archive.GetRawStatistics(1, _base.AddSeconds(50), _base.AddSeconds(60), lookback, _hold);
            Assert.Equal(0, empty.Count);
            Assert.Equal(100, empty.Min);
            Assert.Equal(100, empty.Max);
            Assert.Equal(100, empty.Mean);
            Assert.Equal(0, empty.StdDev);
            // Without a lookback that reaches it, a tag without values and an empty range there is nothing
            var lost = _archive.GetRawStatistics(1, _base.AddSeconds(50), _base.AddSeconds(60), TimeSpan.FromSeconds(2), _hold);
            Assert.Equal(0, lost.Count);
            Assert.Null(lost.Min);
            Assert.Null(lost.Mean);
            Assert.Equal(0, _archive.GetRawStatistics(99, _base, _base.AddMinutes(1), lookback, _hold).Count);
            Assert.Null(_archive.GetRawStatistics(99, _base, _base.AddMinutes(1), lookback, _hold).Min);
            Assert.Null(_archive.GetRawStatistics(1, _base.AddSeconds(60), _base.AddSeconds(60), lookback, _hold).Min);

            // The signal where a step lies in the range: 97.78 holds from the start (0.488) to 0.515, 99.98 until 0.536
            var steps = _archive.GetRawStatistics(12, _base.AddSeconds(0.488), _base.AddSeconds(0.536), lookback, _hold);
            Assert.Equal(1, steps.Count);
            Assert.Equal(97.78, steps.Min.Value, 6);
            Assert.Equal(99.98, steps.Max.Value, 6);
            var expectedMean = (97.78 * 0.027 + 99.98 * 0.021) / 0.048;
            Assert.Equal(expectedMean, steps.Mean.Value, 4);
            var expectedVariance = (0.027 * Math.Pow(97.78 - expectedMean, 2) + 0.021 * Math.Pow(99.98 - expectedMean, 2)) / 0.048;
            Assert.Equal(Math.Sqrt(expectedVariance), steps.StdDev.Value, 3);

            // A value before an invalid one is lost: [10, 60) of the tag with 3, invalid, 4, invalid has no signal at all
            var allLost = _archive.GetRawStatistics(5, _base.AddSeconds(10), _base.AddSeconds(60), lookback, _hold);
            Assert.Equal(1, allLost.Count);
            Assert.Null(allLost.Min);
            Assert.Null(allLost.Mean);
            Assert.Null(allLost.StdDev);
            // ... and from 25 on, the value at 30 holds until the invalid one at 50 is stored: lost as well
            Assert.Null(_archive.GetRawStatistics(5, _base.AddSeconds(31), _base.AddSeconds(45), lookback, _hold).Min);
        }

        [Fact]
        public void Bool_durations_sum_how_long_each_state_held()
        {
            if (_archive == null)
            {
                return;
            }

            var lookback = TimeSpan.FromDays(30);
            _archive.InsertValues([
                Value(3, 1, 5), Value(3, 0, 20), Value(3, 1, 30), Value(3, double.NaN, 40, ValueState.Invalid), Value(3, 1, 50),
                Value(4, 1, 5), Value(8, 1, 5), Value(8, double.NaN, 40, ValueState.Invalid)]);

            // [10, 60): high 10-20 (from the value before the range), low 20-30, no signal 30-50 (the value at 30 is lost
            // before the invalid one at 40, which holds until 50), high 50-60
            var all = _archive.GetBoolDurations(3, _base.AddSeconds(10), _base.AddSeconds(60), lookback, _hold);
            Assert.Equal(20, all.HighSeconds, 3);
            Assert.Equal(10, all.LowSeconds, 3);
            Assert.Equal(20, all.InvalidSeconds, 3);

            // Without a value before the range its first part is not counted: the first value is at 5
            var early = _archive.GetBoolDurations(3, _base, _base.AddSeconds(25), lookback, _hold);
            Assert.Equal(15, early.HighSeconds, 3);
            Assert.Equal(5, early.LowSeconds, 3);
            Assert.Equal(0, early.InvalidSeconds, 3);

            // A value that does not change holds through a range without values, for the hold time at most (the value at 5
            // holds until 35); a lookback that does not reach it counts nothing
            var held = _archive.GetBoolDurations(4, _base.AddSeconds(10), _base.AddSeconds(30), lookback, _hold);
            Assert.Equal(20, held.HighSeconds, 3);
            var heldOut = _archive.GetBoolDurations(4, _base.AddSeconds(10), _base.AddSeconds(100), lookback, _hold);
            Assert.Equal(25, heldOut.HighSeconds, 3);
            Assert.Equal(65, heldOut.InvalidSeconds, 3);
            var lost = _archive.GetBoolDurations(4, _base.AddSeconds(10), _base.AddSeconds(100), TimeSpan.FromSeconds(2), _hold);
            Assert.Equal(0, lost.HighSeconds, 3);

            // A range without values before an invalid one: the value is lost, the whole range is no signal
            var beforeInvalid = _archive.GetBoolDurations(8, _base.AddSeconds(10), _base.AddSeconds(30), lookback, _hold);
            Assert.Equal(0, beforeInvalid.HighSeconds, 3);
            Assert.Equal(20, beforeInvalid.InvalidSeconds, 3);
            // ... and a range that ends there: the value before the invalid one is lost up to the end of the range
            var tailInvalid = _archive.GetBoolDurations(3, _base.AddSeconds(32), _base.AddSeconds(38), lookback, _hold);
            Assert.Equal(0, tailInvalid.HighSeconds, 3);
            Assert.Equal(6, tailInvalid.InvalidSeconds, 3);

            // Nothing is known about the future: the range is cut at now, and the last value (at 50) holds for the hold time,
            // the rest up to now is no signal
            var untilNow = _archive.GetBoolDurations(3, _base.AddSeconds(50), DateTime.Now.AddHours(5), lookback, _hold);
            Assert.Equal(30, untilNow.HighSeconds, 3);
            var lostSeconds = (DateTime.Now - _base.AddSeconds(80)).TotalSeconds;
            Assert.InRange(untilNow.InvalidSeconds, lostSeconds - 5, lostSeconds + 5);

            // An empty range and a tag without values
            Assert.Equal(new BoolDurations(0, 0, 0), _archive.GetBoolDurations(3, _base.AddSeconds(60), _base.AddSeconds(60), lookback, _hold));
            Assert.Equal(new BoolDurations(0, 0, 0), _archive.GetBoolDurations(99, _base, _base.AddMinutes(1), lookback, _hold));
        }

        [Fact]
        public void Bool_durations_count_the_changes_and_the_longest_and_shortest_times()
        {
            if (_archive == null)
            {
                return;
            }

            var lookback = TimeSpan.FromDays(30);
            _archive.InsertValues([
                Value(11, 0, 5), Value(11, 1, 10), Value(11, 1, 15), Value(11, 0, 20), Value(11, 1, 30), Value(11, 0, 33),
                Value(11, double.NaN, 40, ValueState.Invalid), Value(11, 1, 45), Value(11, 0, 50)]);

            // [7, 60): low 7-10 (from the value before the range), high 10-20 (the value at 15 only repeats it), low 20-30,
            // high 30-33, no signal 33-45 (the value at 33 is lost before the invalid one), high 45-50, low 50-60
            var d = _archive.GetBoolDurations(11, _base.AddSeconds(7), _base.AddSeconds(60), lookback, _hold);
            Assert.Equal(18, d.HighSeconds, 3);
            Assert.Equal(23, d.LowSeconds, 3);
            Assert.Equal(12, d.InvalidSeconds, 3);
            // Changes: low to high at 10, high to low at 20, low to high at 30, high to low at 50; none over the lost signal
            Assert.Equal(4, d.Changes);
            Assert.Equal(10, d.LongestHighSeconds.Value, 3);
            Assert.Equal(10, d.LongestLowSeconds.Value, 3);
            // The shortest only of the runs that began and ended with a change in the range: the high 10-20 and the low 20-30
            // (the high 30-33 ends in the lost signal, the one 45-50 began after it)
            Assert.Equal(10, d.ShortestHighSeconds.Value, 3);
            Assert.Equal(10, d.ShortestLowSeconds.Value, 3);

            // A range inside one run: it is the longest, and no run is complete
            var inside = _archive.GetBoolDurations(11, _base.AddSeconds(21), _base.AddSeconds(28), lookback, _hold);
            Assert.Equal(0, inside.Changes);
            Assert.Equal(7, inside.LongestLowSeconds.Value, 3);
            Assert.Null(inside.ShortestLowSeconds);
            Assert.Null(inside.LongestHighSeconds);
            Assert.Null(inside.ShortestHighSeconds);
        }

        [Fact]
        public void Invalid_seconds_sum_how_long_there_was_no_signal()
        {
            if (_archive == null)
            {
                return;
            }

            var lookback = TimeSpan.FromDays(30);
            _archive.InsertValues([
                Value(5, 3, 5), Value(5, double.NaN, 20, ValueState.Invalid), Value(5, 4, 30), Value(5, double.NaN, 50, ValueState.Invalid),
                Value(6, double.NaN, 5, ValueState.Invalid), Value(6, 7, 25), Value(7, 1, 5),
                // A service that was down: valid, then nothing, then an invalid row at its start and valid values again
                Value(9, 5, 10), Value(9, 6, 20), Value(9, double.NaN, 70, ValueState.Invalid), Value(9, 7, 71),
                Value(10, 1, 5), Value(10, double.NaN, 40, ValueState.Invalid)]);

            // [10, 60): the value at 5 is lost before the invalid one at 20, 20-30 invalid, the value at 30 is lost before
            // the invalid one at 50, which holds to the end: the whole range
            Assert.Equal(50, _archive.GetInvalidSeconds(5, _base.AddSeconds(10), _base.AddSeconds(60), lookback, _hold).Value, 3);
            // A range that ends before the next value cuts the segment
            Assert.Equal(15, _archive.GetInvalidSeconds(5, _base.AddSeconds(10), _base.AddSeconds(25), lookback, _hold).Value, 3);
            // The value that holds at the start was invalid: 10-25; the value at 25 is the newest and holds until 55
            Assert.Equal(15, _archive.GetInvalidSeconds(6, _base.AddSeconds(10), _base.AddSeconds(50), lookback, _hold).Value, 3);
            Assert.Equal(20, _archive.GetInvalidSeconds(6, _base.AddSeconds(10), _base.AddSeconds(60), lookback, _hold).Value, 3);
            // A tag that never was invalid (its only value at 5 holds until 35), a tag without values and an empty range
            Assert.Equal(0, _archive.GetInvalidSeconds(7, _base.AddSeconds(10), _base.AddSeconds(30), lookback, _hold).Value, 3);
            Assert.Equal(25, _archive.GetInvalidSeconds(7, _base.AddSeconds(10), _base.AddSeconds(60), lookback, _hold).Value, 3);
            Assert.Equal(0, _archive.GetInvalidSeconds(99, _base, _base.AddMinutes(1), lookback, _hold).Value, 3);
            Assert.Equal(0, _archive.GetInvalidSeconds(5, _base.AddSeconds(60), _base.AddSeconds(60), lookback, _hold).Value, 3);
            // The time from the last valid value before the invalid one is lost, also when nothing was stored in between
            Assert.Equal(50.0 + 1, _archive.GetInvalidSeconds(9, _base.AddSeconds(15), _base.AddSeconds(80), lookback, _hold).Value, 3);
            // The range of a value that is lost, with the invalid row in it and before it
            Assert.Equal(20, _archive.GetInvalidSeconds(9, _base.AddSeconds(30), _base.AddSeconds(50), lookback, _hold).Value, 0);
            // A range that ends before the invalid row: the value before it is lost up to the end of the range
            Assert.Equal(6, _archive.GetInvalidSeconds(10, _base.AddSeconds(32), _base.AddSeconds(38), lookback, _hold).Value, 3);
            Assert.Equal(20, _archive.GetInvalidSeconds(10, _base.AddSeconds(10), _base.AddSeconds(30), lookback, _hold).Value, 3);
            // Nothing is known about the future: the last invalid value holds until now at most
            var untilNow = _archive.GetInvalidSeconds(5, _base.AddSeconds(50), DateTime.Now.AddHours(5), lookback, _hold).Value;
            Assert.InRange(untilNow, (DateTime.Now - _base.AddSeconds(50)).TotalSeconds - 5, (DateTime.Now - _base.AddSeconds(50)).TotalSeconds + 5);
            // Same as the bool durations
            foreach (var tag in new[] { 5, 6, 9, 10 })
            {
                Assert.Equal(
                    _archive.GetBoolDurations(tag, _base.AddSeconds(12), _base.AddSeconds(75), lookback, _hold).InvalidSeconds,
                    _archive.GetInvalidSeconds(tag, _base.AddSeconds(12), _base.AddSeconds(75), lookback, _hold).Value,
                    3);
            }
        }

        [Fact]
        public void Statistics_stop_counting_when_the_last_value_stops_holding()
        {
            if (_archive == null)
            {
                return;
            }

            var lookback = TimeSpan.FromDays(30);
            _archive.InsertValues([
                // Numbers and bools whose last row is valid: nothing was stored after it
                Value(30, 2, 10), Value(30, 4, 20), Value(31, 1, 10), Value(31, 0, 20),
                // The last row is invalid: there is no signal from it on anyway
                Value(32, 1, 10), Value(32, double.NaN, 20, ValueState.Invalid)]);
            _archive.UtcNow = () => _base.AddSeconds(300).ToUniversalTime();

            // [0, 200): 2 holds 10-20, 4 holds 20-50 (the last value holds 30 s), nothing counts after that
            var stats = _archive.GetRawStatistics(30, _base, _base.AddSeconds(200), lookback, _hold);
            Assert.Equal(2, stats.Count);
            Assert.Equal(2, stats.Min);
            Assert.Equal(4, stats.Max);
            Assert.Equal((2 * 10 + 4 * 30) / 40.0, stats.Mean.Value, 9);
            // A range that ends while the last value holds is not cut
            var early = _archive.GetRawStatistics(30, _base, _base.AddSeconds(40), lookback, _hold);
            Assert.Equal((2 * 10 + 4 * 20) / 30.0, early.Mean.Value, 9);
            // A range after it has no signal and no values
            var after = _archive.GetRawStatistics(30, _base.AddSeconds(60), _base.AddSeconds(200), lookback, _hold);
            Assert.Equal(0, after.Count);
            Assert.Null(after.Min);
            Assert.Null(after.Mean);
            // The hold time of a tag with a long cycle is longer: 4 holds 20-140
            var slow = _archive.GetRawStatistics(30, _base, _base.AddSeconds(200), lookback, DataLogLib.Common.ArchiveHold.For(60_000));
            Assert.Equal((2 * 10 + 4 * 120) / 130.0, slow.Mean.Value, 9);

            // The bool: high 10-20, low 20-50, the rest of the range 50-200 is no signal
            var bools = _archive.GetBoolDurations(31, _base, _base.AddSeconds(200), lookback, _hold);
            Assert.Equal(10, bools.HighSeconds, 3);
            Assert.Equal(30, bools.LowSeconds, 3);
            Assert.Equal(150, bools.InvalidSeconds, 3);
            Assert.Equal(1, bools.Changes);
            // The low time ended in the lost signal: it is the longest, and no shortest one is complete
            Assert.Equal(30, bools.LongestLowSeconds.Value, 3);
            Assert.Null(bools.ShortestLowSeconds);
            Assert.Equal(new BoolDurations(0, 0, 140), _archive.GetBoolDurations(31, _base.AddSeconds(60), _base.AddSeconds(200), lookback, _hold) with { Changes = 0 });
            var boolsEarly = _archive.GetBoolDurations(31, _base, _base.AddSeconds(40), lookback, _hold);
            Assert.Equal(20, boolsEarly.LowSeconds, 3);
            Assert.Equal(0, boolsEarly.InvalidSeconds, 3);

            // The seconds without signal: 50-200, or all of a range after the last value, and none while it holds
            Assert.Equal(150, _archive.GetInvalidSeconds(30, _base, _base.AddSeconds(200), lookback, _hold).Value, 3);
            Assert.Equal(140, _archive.GetInvalidSeconds(30, _base.AddSeconds(60), _base.AddSeconds(200), lookback, _hold).Value, 3);
            Assert.Equal(0, _archive.GetInvalidSeconds(30, _base, _base.AddSeconds(40), lookback, _hold).Value, 3);
            // An invalid last row is no signal from its time on, nothing is added
            Assert.Equal(190, _archive.GetInvalidSeconds(32, _base, _base.AddSeconds(200), lookback, _hold).Value, 3);
            Assert.Equal(190, _archive.GetBoolDurations(32, _base, _base.AddSeconds(200), lookback, _hold).InvalidSeconds, 3);

            // Now cuts the range first: at 100 the last value stopped holding at 50, at 40 it still holds
            _archive.UtcNow = () => _base.AddSeconds(100).ToUniversalTime();
            Assert.Equal(50, _archive.GetInvalidSeconds(30, _base, _base.AddSeconds(200), lookback, _hold).Value, 3);
            _archive.UtcNow = () => _base.AddSeconds(40).ToUniversalTime();
            Assert.Equal(0, _archive.GetInvalidSeconds(30, _base, _base.AddSeconds(200), lookback, _hold).Value, 3);
            Assert.Equal((2 * 10 + 4 * 20) / 30.0, _archive.GetRawStatistics(30, _base, _base.AddSeconds(200), lookback, _hold).Mean.Value, 9);
        }

        [Fact]
        public void Retention_applies_to_every_archive_table()
        {
            if (_archive == null)
            {
                return;
            }

            try
            {
                var (version, retention) = _archive.ReadMetadata();
                Assert.StartsWith("TimescaleDB ", version);
                Assert.Equal(NinetyDaysSeconds, retention);

                _archive.SetRetentionSeconds(30L * 24 * 3600);
                Assert.Equal(30L * 24 * 3600, _archive.ReadMetadata().RetentionSeconds);
                Assert.Equal("13", Scalar(
                    "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' "
                    + "AND hypertable_name LIKE 'archive%' "
                    + "AND extract(epoch FROM (config->>'drop_after')::interval) = 30 * 24 * 3600"));

                _archive.SetRetentionSeconds(0);
                Assert.Equal(0, _archive.ReadMetadata().RetentionSeconds);
                Assert.Equal("0", Scalar(
                    "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' "
                    + "AND hypertable_name LIKE 'archive%'"));
                Assert.Throws<ArgumentException>(() => _archive.SetRetentionSeconds(60));
            }
            finally
            {
                _archive.SetRetentionSeconds(NinetyDaysSeconds);
            }
        }

        // Range ends around minutes, hours, the counted range and the values, in seconds after _base
        private static readonly double[] CountOffsets =
            [-4000, -3700, -30, 0, 1.5, 59.999, 60, 61, 3599, 3600, 3601.25, 5400, 7199, 7260.5, 9000, 9600, 9601, 9700, 12000];

        [Fact]
        public void Raw_value_counts_from_the_counters_equal_the_count_of_the_raw_values()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddHours(-1), _base.AddHours(-1));
            Assert.True(_archive.RollUpCounts(_base.AddSeconds(9600).ToUniversalTime()));

            Assert.Equal(_base.AddSeconds(9600).ToUniversalTime(), ReadCountTime(TimescaleArchive.CountUntilKey));
            Assert.Equal(RawCount(1, _base.AddHours(-1), _base.AddSeconds(9600)) + RawCount(2, _base.AddHours(-1), _base.AddSeconds(9600)),
                long.Parse(Scalar($"SELECT sum(count) FROM {TimescaleArchive.MinuteCountTable}")));
            // The full hours of the counted range with values: two of each tag, each the sum of its minutes
            Assert.Equal("4", Scalar($"SELECT count(*) FROM {TimescaleArchive.HourCountTable}"));
            Assert.Equal("0", Scalar(
                $"SELECT count(*) FROM {TimescaleArchive.HourCountTable} h WHERE h.count <> (SELECT sum(m.count) "
                + $"FROM {TimescaleArchive.MinuteCountTable} m WHERE m.tag_id = h.tag_id AND date_trunc('hour', m.time, 'UTC') = h.time)"));
            AssertCountsEqualRawCounts(1, 2);

            // The full hours are read from the hour rows
            Execute($"UPDATE {TimescaleArchive.HourCountTable} SET count = count + 1000 "
                + $"WHERE tag_id = (SELECT id FROM archivetag WHERE guid = '{Uid(1)}') AND time = '{_base.ToUniversalTime():O}'");
            Assert.Equal(RawCount(1, _base, _base.AddHours(2)) + 1000, _archive.GetRawValueCount(1, _base, _base.AddHours(2)));
            Assert.Equal(RawCount(1, _base.AddSeconds(1), _base.AddHours(2)), _archive.GetRawValueCount(1, _base.AddSeconds(1), _base.AddHours(2)));
        }

        [Fact]
        public void The_roll_up_after_a_restart_counts_the_values_written_before_it()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddHours(-1), _base.AddHours(-1));
            // A new process does not know the tags written by the one before
            var restarted = new TimescaleArchive();
            restarted.Init(PostgresTestConfiguration.Create());
            restarted.SetUidLookup(new UidLookup());

            Assert.True(restarted.RollUpCounts(_base.AddSeconds(9600).ToUniversalTime()));

            Assert.Equal(RawCount(1, _base.AddHours(-1), _base.AddSeconds(9600)) + RawCount(2, _base.AddHours(-1), _base.AddSeconds(9600)),
                long.Parse(Scalar($"SELECT sum(count) FROM {TimescaleArchive.MinuteCountTable}")));
            AssertCountsEqualRawCounts(1, 2);
        }

        [Fact]
        public void Values_written_while_a_step_counts_its_window_are_counted_once()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddSeconds(3600), _base.AddSeconds(3600));
            // Into the window of the running step (an existing and a new minute, a new tag) and outside of it
            List<ArchiveValue> written = [Value(1, 1, 3610.5), Value(2, 2, 3700.25), Value(3, 3, 4000), Value(1, 4, 100.5)];
            _archive.StepCounted = () =>
            {
                _archive.StepCounted = null;
                _archive.InsertValues(written);
            };
            Assert.True(_archive.RollUpCounts(_base.AddSeconds(4200).ToUniversalTime()));
            AssertCountsEqualRawCounts(1, 2, 3);

            // The same during a backfill step
            written = [Value(1, 5, 1800.5), Value(3, 6, 2000), Value(2, 7, 4300)];
            _archive.StepCounted = () =>
            {
                _archive.StepCounted = null;
                _archive.InsertValues(written);
            };
            Assert.Equal(_base.AddSeconds(1800).ToUniversalTime(),
                _archive.BackfillCountsStep(_base.AddSeconds(1800).ToUniversalTime(), TimeSpan.FromHours(1)));
            AssertCountsEqualRawCounts(1, 2, 3);
        }

        [Fact]
        public void A_window_with_many_minutes_written_meanwhile_is_counted_again()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddSeconds(3600), _base.AddSeconds(3600));
            // 21 tags in 10 minutes of the window: more minutes than are recounted under the lock
            List<ArchiveValue> written = [];
            for (var tagId = 10; tagId <= 30; tagId++)
            {
                for (var minute = 0; minute < 10; minute++)
                {
                    written.Add(Value(tagId, minute, 3600 + minute * 60 + 1.5));
                }
            }
            Assert.True(written.Count > TimescaleArchive.MaxRecountMinutes);
            var calls = 0;
            _archive.StepCounted = () =>
            {
                if (++calls == 1)
                {
                    _archive.InsertValues(written);
                }
            };

            Assert.True(_archive.RollUpCounts(_base.AddSeconds(4200).ToUniversalTime()));

            Assert.Equal(2, calls);
            AssertCountsEqualRawCounts(1, 2, 10, 20, 30);
        }

        [Fact]
        public void The_last_value_of_a_tag_is_found_in_every_part_of_the_archive()
        {
            if (_archive == null)
            {
                return;
            }

            var now = DateTime.Now;
            // The counted range starts 5 days back, 20 minutes into an hour
            var from = _base.AddDays(-5).AddMinutes(20);
            List<ArchiveValue> values =
            [
                // In full hours of the counted range, days back
                Value(1, 1, -3 * 86400 + 1800), Value(1, 2, -3 * 86400 + 1812.5),
                // Before the counted range
                Value(2, 3, -6 * 86400),
                // In the partial hour at the start of the range
                Value(3, 4, -5 * 86400 + 25 * 60.0),
                // In the last hour and in the last day
                new() { Id = 4, Value = 5, Time = now.AddMinutes(-10), State = ValueState.Valid },
                new() { Id = 5, Value = 6, Time = now.AddHours(-5), State = ValueState.Valid },
                // In the last hours of the range and one hour after the 2 days
                Value(7, 7, -2 * 86400 + 600), Value(7, 8, -1.5 * 86400),
            ];
            _archive.InsertValues(values);
            var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Local);
            SetCountRange(minute, minute);
            while (_archive.BackfillCountsStep(from.ToUniversalTime(), TimeSpan.FromHours(12)) > from.ToUniversalTime())
            {
            }

            Assert.Equal(_base.AddSeconds(-3 * 86400 + 1812.5), _archive.GetArchiveEndTime(1));
            Assert.Equal(_base.AddSeconds(-6 * 86400), _archive.GetArchiveEndTime(2));
            Assert.Equal(_base.AddSeconds(-5 * 86400 + 25 * 60.0), _archive.GetArchiveEndTime(3));
            Assert.Equal(TimescaleArchive.FromDbTime(TimescaleArchive.ToDbTime(now.AddMinutes(-10))), _archive.GetArchiveEndTime(4));
            Assert.Equal(TimescaleArchive.FromDbTime(TimescaleArchive.ToDbTime(now.AddHours(-5))), _archive.GetArchiveEndTime(5));
            Assert.Equal(DateTime.MinValue, _archive.GetArchiveEndTime(6));
            Assert.Equal(_base.AddSeconds(-1.5 * 86400), _archive.GetArchiveEndTime(7));

            // Not rolled up for 2 days (after a downtime): the values after the range are searched in archiveraw
            SetCountRange(from, _base.AddDays(-2));
            Assert.Equal(_base.AddSeconds(-1.5 * 86400), _archive.GetArchiveEndTime(7));
            Assert.Equal(_base.AddSeconds(-3 * 86400 + 1812.5), _archive.GetArchiveEndTime(1));
            Assert.Equal(_base.AddSeconds(-6 * 86400), _archive.GetArchiveEndTime(2));
            // The first value of the system tag 1, searched from the oldest chunk on
            Assert.Equal(_base.AddSeconds(-3 * 86400 + 1800), _archive.GetArchiveStartTime());
        }

        [Fact]
        public void Late_raw_values_are_counted_in_the_transaction_that_writes_them()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddHours(-1), _base.AddHours(-1));
            _archive.RollUpCounts(_base.AddSeconds(9600).ToUniversalTime());

            // Into a counted minute, a new minute and hour of a new tag, before the range and after it
            _archive.InsertValues([Value(1, 1, 100.5), Value(1, 2, 100.5), Value(3, 3, 4000), Value(1, 4, -4000), Value(1, 5, 9650)]);

            Assert.Equal("1", Scalar(
                $"SELECT count FROM {TimescaleArchive.HourCountTable} WHERE tag_id = (SELECT id FROM archivetag WHERE guid = '{Uid(3)}')"));
            Assert.Equal("0", Scalar(
                $"SELECT count(*) FROM {TimescaleArchive.MinuteCountTable} WHERE time < '{_base.AddHours(-1).ToUniversalTime():O}'"));
            AssertCountsEqualRawCounts(1, 2, 3);

            _archive.RollUpCounts(_base.AddSeconds(9720).ToUniversalTime());
            AssertCountsEqualRawCounts(1, 3);
        }

        [Fact]
        public void Late_raw_values_are_counted_into_compressed_counter_chunks()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddHours(-1), _base.AddHours(-1));
            _archive.RollUpCounts(_base.AddSeconds(9600).ToUniversalTime());
            Execute($"SELECT compress_chunk(c) FROM show_chunks('{TimescaleArchive.MinuteCountTable}') c");
            Execute($"SELECT compress_chunk(c) FROM show_chunks('{TimescaleArchive.HourCountTable}') c");

            _archive.InsertValues([Value(1, 1, 100.5), Value(2, 2, 3700.5), Value(3, 3, 4000), Value(3, 3, 4001)]);

            Assert.Equal("2", Scalar(
                $"SELECT count FROM {TimescaleArchive.HourCountTable} WHERE tag_id = (SELECT id FROM archivetag WHERE guid = '{Uid(3)}')"));
            AssertCountsEqualRawCounts(1, 2, 3);
        }

        [Fact]
        public void The_backfill_counts_the_raw_values_before_the_counted_range()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddSeconds(9000), _base.AddSeconds(9000));
            var target = _base.AddHours(-1).ToUniversalTime();
            DateTime? from;
            var steps = 0;
            do
            {
                from = _archive.BackfillCountsStep(target, TimeSpan.FromMinutes(7));
                steps++;
                // Every step leaves the counters exact
                if (steps % 10 == 0)
                {
                    AssertCountsEqualRawCounts(1);
                }
            }
            while (from > target);

            Assert.Equal(target, from);
            Assert.Equal(target, _archive.BackfillCountsStep(target, TimeSpan.FromMinutes(7)));
            Assert.Equal(RawCount(1, _base.AddHours(-1), _base.AddSeconds(9000)) + RawCount(2, _base.AddHours(-1), _base.AddSeconds(9000)),
                long.Parse(Scalar($"SELECT sum(count) FROM {TimescaleArchive.MinuteCountTable}")));
            Assert.Equal("4", Scalar($"SELECT count(*) FROM {TimescaleArchive.HourCountTable}"));
            AssertCountsEqualRawCounts(1, 2);
        }

        [Fact]
        public void Raw_value_counts_stay_exact_when_the_retention_drops_raw_chunks()
        {
            if (_archive == null)
            {
                return;
            }

            InsertCountValues();
            SetCountRange(_base.AddHours(-1), _base.AddHours(-1));
            _archive.RollUpCounts(_base.AddSeconds(9600).ToUniversalTime());
            var before = RawCount(1, _base, _base.AddHours(3));

            try
            {
                _archive.SetRetentionSeconds(3600);
                Assert.True(RawCount(1, _base, _base.AddHours(3)) < before);
                AssertCountsEqualRawCounts(1, 2);

                // Without any raw chunk the counters count nothing
                Execute("SELECT drop_chunks('archiveraw', older_than => now() + interval '1 day')");
                AssertCountsEqualRawCounts(1, 2);
            }
            finally
            {
                _archive.SetRetentionSeconds(NinetyDaysSeconds);
            }
        }

        [Fact]
        public void Application_events_are_written_to_their_table()
        {
            if (_archive == null)
            {
                return;
            }

            var store = new AppEventStore(PostgresTestConfiguration.Create());
            store.InsertEventPoints([
                new EventLogPoint { TimeUtc = DateTime.UtcNow, Level = "Error", Component = "archive", Message = "first" },
                new EventLogPoint { TimeUtc = DateTime.UtcNow, Message = "second" }]);

            Assert.Equal("2", Scalar($"SELECT count(*) FROM {AppEventStore.TableName}"));
            Assert.Equal("archive", Scalar($"SELECT component FROM {AppEventStore.TableName} WHERE message = 'first'"));
        }

        private static string Uid(int tagId) => tagId.ToString("D32");

        private ArchiveValue Value(int tagId, double value, double seconds, ValueState state = ValueState.Valid,
            CompressionType compression = CompressionType.RAW)
            => new() { Id = tagId, Value = value, Time = _base.AddSeconds(seconds), State = state, Compression = compression };

        private List<ArchiveValue> ReadRaw(int tagId, DateTime start, DateTime end)
        {
            List<ArchiveValue> values = [];
            _archive.ReadRawValues(Uid(tagId), start, end, values.Add);
            return values;
        }

        /// <summary>
        /// Tag 1 every 7 s with a duplicate time and an invalid NaN, tag 2 every 13 s, both over 2.5 hours from _base.
        /// </summary>
        private void InsertCountValues()
        {
            List<ArchiveValue> values = [];
            for (var seconds = 0; seconds <= 9000; seconds += 7)
            {
                values.Add(Value(1, seconds, seconds));
            }
            for (var seconds = 0; seconds <= 9000; seconds += 13)
            {
                values.Add(Value(2, seconds, seconds));
            }
            values.Add(Value(1, 70, 70));
            values.Add(Value(1, double.NaN, 71.5, ValueState.Invalid));
            _archive.InsertValues(values);
        }

        private void AssertCountsEqualRawCounts(params int[] tagIds)
        {
            foreach (var tagId in tagIds)
            {
                for (var i = 0; i < CountOffsets.Length; i++)
                {
                    for (var j = i; j < CountOffsets.Length; j++)
                    {
                        var start = _base.AddSeconds(CountOffsets[i]);
                        var end = _base.AddSeconds(CountOffsets[j]);
                        Assert.True(RawCount(tagId, start, end) == _archive.GetRawValueCount(tagId, start, end),
                            $"Tag {tagId} from {CountOffsets[i]} s to {CountOffsets[j]} s: "
                            + $"{_archive.GetRawValueCount(tagId, start, end)} instead of {RawCount(tagId, start, end)}");
                    }
                }
            }
        }

        private static long RawCount(int tagId, DateTime start, DateTime end)
        {
            using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
            using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM archiveraw r JOIN archivetag t ON t.id = r.tag_id "
                + "WHERE t.guid = @uid AND r.time >= @start AND r.time < @end");
            command.Parameters.AddWithValue("uid", Uid(tagId));
            command.Parameters.AddWithValue("start", TimescaleArchive.ToDbTime(start));
            command.Parameters.AddWithValue("end", TimescaleArchive.ToDbTime(end));
            return (long)command.ExecuteScalar();
        }

        private static void SetCountRange(DateTime from, DateTime until)
        {
            Execute($"UPDATE parameter SET value = '{new DateTimeOffset(from).ToUnixTimeSeconds()}' WHERE key = '{TimescaleArchive.CountFromKey}'");
            Execute($"UPDATE parameter SET value = '{new DateTimeOffset(until).ToUnixTimeSeconds()}' WHERE key = '{TimescaleArchive.CountUntilKey}'");
        }

        [Fact]
        public void The_load_guard_pauses_the_policies_and_schedules_them_again()
        {
            if (_archive == null)
            {
                return;
            }

            const string Scheduled = "SELECT count(*) FILTER (WHERE scheduled) || '/' || count(*) FROM timescaledb_information.jobs "
                + "WHERE proc_name IN ('policy_compression', 'policy_retention')";
            Execute($"DELETE FROM parameter WHERE key = '{TimescaleArchive.PausedJobsKey}'");
            // A policy unscheduled by hand stays so
            var byHand = Scalar("SELECT min(job_id) FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention'");
            Execute($"SELECT alter_job({byHand}, scheduled => false)");
            try
            {
                var all = Scalar(Scheduled).Split('/')[1];
                _archive.ApplyLoadLevel(LoadLevel.Tense);
                Assert.Equal($"0/{all}", Scalar(Scheduled));
                // A second change while paused keeps the stored ids
                _archive.ApplyLoadLevel(LoadLevel.Critical);
                Assert.Equal(int.Parse(all) - 1, Scalar($"SELECT value FROM parameter WHERE key = '{TimescaleArchive.PausedJobsKey}'").Split(',').Length);

                // A restart while paused (a new archive) schedules them again
                var restarted = new TimescaleArchive();
                restarted.Init(PostgresTestConfiguration.Create());
                restarted.ResumePausedJobs();
                Assert.Equal($"{int.Parse(all) - 1}/{all}", Scalar(Scheduled));
                Assert.Equal("False", Scalar($"SELECT scheduled FROM timescaledb_information.jobs WHERE job_id = {byHand}"));
                Assert.Equal("", Scalar($"SELECT value FROM parameter WHERE key = '{TimescaleArchive.PausedJobsKey}'"));

                _archive.ApplyLoadLevel(LoadLevel.Tense);
                _archive.ApplyLoadLevel(LoadLevel.Normal);
                Assert.Equal($"{int.Parse(all) - 1}/{all}", Scalar(Scheduled));
            }
            finally
            {
                Execute($"SELECT alter_job({byHand}, scheduled => true)");
            }
        }

        [Fact]
        public void The_load_guard_lets_a_running_compression_finish_unless_the_recording_is_at_risk()
        {
            if (_archive == null)
            {
                return;
            }

            Execute($"DELETE FROM parameter WHERE key = '{TimescaleArchive.PausedJobsKey}'");
            var job = Scalar("SELECT job_id FROM timescaledb_information.jobs WHERE proc_name = 'policy_compression' "
                + "AND hypertable_name = 'archiveraw'");
            var name = Scalar($"SELECT application_name FROM timescaledb_information.jobs WHERE job_id = {job}");
            // A session under the name of the job stands for its background worker while it compresses a chunk
            var builder = new NpgsqlConnectionStringBuilder(TestPostgres.ConnectionString) { ApplicationName = name, Pooling = false };
            using var running = new NpgsqlConnection(builder.ConnectionString);
            running.Open();
            using var sleep = new NpgsqlCommand("SELECT pg_sleep(30)", running) { CommandTimeout = 60 };
            var compressing = sleep.ExecuteNonQueryAsync();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (Scalar($"SELECT count(*) FROM pg_stat_activity WHERE application_name = '{name}' AND state = 'active'") == "0")
                {
                    Assert.True(DateTime.UtcNow < deadline, "the session of the job does not run");
                    Thread.Sleep(50);
                }

                // Tense and critical by the load alone pause the other policies, the running one goes on
                _archive.ApplyLoadLevel(LoadLevel.Tense);
                _archive.ApplyLoadLevel(LoadLevel.Critical);
                Assert.Equal("True", Scalar($"SELECT scheduled FROM timescaledb_information.jobs WHERE job_id = {job}"));
                Assert.Equal("0", Scalar("SELECT count(*) FROM timescaledb_information.jobs "
                    + $"WHERE proc_name IN ('policy_compression', 'policy_retention') AND scheduled AND job_id <> {job}"));
                Assert.False(compressing.IsCompleted);

                // At risk, it is cancelled
                _archive.ApplyLoadLevel(LoadLevel.Critical, recordingAtRisk: true);
                Assert.ThrowsAny<Exception>(() => compressing.GetAwaiter().GetResult());
                Assert.Equal("True", Scalar($"SELECT scheduled FROM timescaledb_information.jobs WHERE job_id = {job}"));

                // Once it is done (cancelled here; the worker of the job exits then), the next check pauses it as well
                running.Close();
                _archive.ApplyLoadLevel(LoadLevel.Tense);
                Assert.Equal("False", Scalar($"SELECT scheduled FROM timescaledb_information.jobs WHERE job_id = {job}"));

                // Normal: scheduled again, and the cancelled one starts at once
                Execute($"SELECT alter_job({job}, next_start => now() + interval '1 hour')");
                _archive.ApplyLoadLevel(LoadLevel.Normal);
                Assert.Equal("True", Scalar($"SELECT scheduled FROM timescaledb_information.jobs WHERE job_id = {job}"));
                Assert.Equal("True", Scalar($"SELECT next_start <= now() FROM timescaledb_information.jobs WHERE job_id = {job}"));
            }
            finally
            {
                _archive.ResumePausedJobs();
            }
        }

        private static DateTime ReadCountTime(string key)
            => DateTimeOffset.FromUnixTimeSeconds(long.Parse(Scalar($"SELECT value FROM parameter WHERE key = '{key}'"))).UtcDateTime;

        private static void Execute(string sql)
        {
            using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
            using var command = dataSource.CreateCommand(sql);
            command.ExecuteNonQuery();
        }

        private static string Scalar(string sql)
        {
            using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
            using var command = dataSource.CreateCommand(sql);
            return Convert.ToString(command.ExecuteScalar());
        }
    }
}
