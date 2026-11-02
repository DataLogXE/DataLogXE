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
using Serilog;

namespace DataLogService.DataSource
{
    /// <summary>
    /// Counts archive inserts of one data source and writes ValueCountPerSec every cycle.
    /// </summary>
    internal sealed class DataSourceThroughput : IDisposable
    {
        #region public const
        public const string Address = "ValueCountPerSec";

        public static readonly int SparklineSampleCapacity =
            (Consts.DATASOURCE_THROUGHPUT_SPARKLINE_MINUTES * 60 * 1000) / Consts.VALUES_PER_SECOND_CYCLE_MS;
        #endregion

        #region private fields
        private readonly object _gate = new();
        private readonly double[] _samples = new double[SparklineSampleCapacity];
        private Action<ArchiveValue> _insert;
        private Timer _timer;
        private int _tagId;
        private int _insertCount;
        private int _closed;
        private int _sampleWriteIndex;
        private int _sampleCount;
        private double? _lastRate;
        #endregion

        #region public methods
        public void Start(DataSourceModel dataSource, Action<ArchiveValue> insert)
        {
            if (dataSource == null || dataSource.IsActive == 0 || insert == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_closed != 0 || _tagId > 0)
                {
                    return;
                }

                _insert = insert;
            }

            DataTagModel tag;
            try
            {
                tag = EnsureTag(dataSource);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ValueCountPerSec tag setup failed for {DsName} Id: {DsId}", dataSource.Name, dataSource.Id);
                return;
            }

            if (tag == null || tag.Id <= 0)
            {
                Log.Error("ValueCountPerSec tag was not created for {DsName} Id: {DsId}", dataSource.Name, dataSource.Id);
                return;
            }

            Action<ArchiveValue> insertForStartup;
            lock (_gate)
            {
                insertForStartup = _insert;
            }

            if (insertForStartup != null)
            {
                DataSourceBase.InsertStartupArchiveValue(tag.Id, insertForStartup);
            }

            lock (_gate)
            {
                if (_closed != 0)
                {
                    return;
                }

                _tagId = tag.Id;
                _timer = new Timer(OnTick, null, Consts.VALUES_PER_SECOND_CYCLE_MS, Consts.VALUES_PER_SECOND_CYCLE_MS);
            }
        }

        public ThroughputSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                if (_sampleCount == 0)
                {
                    return new ThroughputSnapshot { LastValue = _lastRate, Series = [] };
                }

                var series = new double[_sampleCount];
                if (_sampleCount < SparklineSampleCapacity)
                {
                    Array.Copy(_samples, 0, series, 0, _sampleCount);
                }
                else
                {
                    var tail = SparklineSampleCapacity - _sampleWriteIndex;
                    Array.Copy(_samples, _sampleWriteIndex, series, 0, tail);
                    Array.Copy(_samples, 0, series, tail, _sampleWriteIndex);
                }

                return new ThroughputSnapshot { LastValue = _lastRate, Series = series };
            }
        }

        public void NoteInsert(int dataTagId)
        {
            if (dataTagId <= 0 || Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            var tagId = Volatile.Read(ref _tagId);
            if (tagId <= 0 || dataTagId == tagId)
            {
                return;
            }

            Interlocked.Increment(ref _insertCount);
        }

        public void Dispose()
        {
            Timer timer;
            lock (_gate)
            {
                timer = _timer;
                _timer = null;
            }

            if (timer != null)
            {
                using var done = new ManualResetEvent(false);
                timer.Dispose(done);
                done.WaitOne();
            }

            int tagId;
            Action<ArchiveValue> insert;
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                {
                    return;
                }

                tagId = _tagId;
                insert = _insert;
            }

            if (tagId > 0 && insert != null)
            {
                Write(insert, tagId, 0, ValueState.Invalid);
            }
        }
        #endregion

        #region private methods
        private void OnTick(object state)
        {
            int tagId;
            int count;
            Action<ArchiveValue> insert;
            lock (_gate)
            {
                if (_closed != 0 || _tagId <= 0 || _insert == null)
                {
                    return;
                }

                tagId = _tagId;
                insert = _insert;
                count = Interlocked.Exchange(ref _insertCount, 0);
            }

            var seconds = Consts.VALUES_PER_SECOND_CYCLE_MS / 1000.0;
            var rate = count / seconds;
            RecordSample(rate);
            Write(insert, tagId, rate, ValueState.Valid);
        }

        private void RecordSample(double rate)
        {
            lock (_gate)
            {
                _lastRate = rate;
                _samples[_sampleWriteIndex] = rate;
                _sampleWriteIndex = (_sampleWriteIndex + 1) % SparklineSampleCapacity;
                if (_sampleCount < SparklineSampleCapacity)
                {
                    _sampleCount++;
                }
            }
        }

        private static void Write(Action<ArchiveValue> insert, int tagId, double value, ValueState state)
        {
            try
            {
                insert(new ArchiveValue()
                {
                    Id = tagId,
                    Time = DateTime.Now,
                    Value = value,
                    State = state,
                    Compression = CompressionType.RAW,
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ValueCountPerSec archive write failed for tag {TagId}", tagId);
            }
        }

        private static DataTagModel EnsureTag(DataSourceModel dataSource)
        {
            IDataAccess da = Global.Service.DataAccess;
            var model = new DataTagModel()
            {
                DataSourceId = dataSource.Id,
                Address = Address,
                Name = Address,
                IsActive = 1,
                IsSys = 1,
                IsDeleted = 0,
                TypeId = (int)DataTagType.Number,
                Cycle = Consts.VALUES_PER_SECOND_CYCLE_MS,
            };

            var tag = da.DataTagGetOrCreateByAddress(model);
            if (tag == null || tag.Id <= 0)
            {
                return null;
            }

            if (!NeedsAlign(tag))
            {
                return tag;
            }

            tag.Name = Address;
            tag.Address = Address;
            tag.TypeId = (int)DataTagType.Number;
            tag.Cycle = Consts.VALUES_PER_SECOND_CYCLE_MS;
            tag.IsActive = 1;
            tag.IsSys = 1;
            da.DataTagsUpdate(new List<DataTagModel> { tag });
            if (tag.IsDeleted != 0)
            {
                da.DataTagsUpdateDeleted(new List<int> { tag.Id }, 0);
                tag.IsDeleted = 0;
            }

            return tag;
        }

        private static bool NeedsAlign(DataTagModel tag)
        {
            return tag.Name != Address
                || tag.TypeId != (int)DataTagType.Number
                || tag.Cycle != Consts.VALUES_PER_SECOND_CYCLE_MS
                || tag.IsActive != 1
                || tag.IsSys != 1
                || tag.IsDeleted != 0;
        }
        #endregion
    }
}
