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

using DataLogLib.Models;
using System.Diagnostics;

namespace DataLogService.DataSource.Test
{
    internal enum TestSignalKind
    {
        Sine,
        Square,
        Triangle,
        Saw,
        SineSpike,
        BoolBlink,
        BoolSpike,
    }

    /// <summary>Signal kind with its period (waveforms) or outlier interval (SineSpike) in seconds.</summary>
    internal record TestTypeDef(TestSignalKind Kind, int PeriodSec)
    {
        /// <summary>Boolean data tags (data tag type 1) instead of numbers (type 2).</summary>
        public bool IsBool => Kind is TestSignalKind.BoolBlink or TestSignalKind.BoolSpike;

        /// <summary>Name used as part of the data tag name; plain sine keeps the legacy "Value" name.</summary>
        public string Name => Kind switch
        {
            TestSignalKind.Sine => "Value",
            TestSignalKind.BoolBlink => $"Bool blink {PeriodSec}s",
            TestSignalKind.BoolSpike => $"Bool spike {PeriodSec}s",
            _ => $"{Kind} {PeriodSec}s",
        };
    }

    internal static class TestTypes
    {
        private static readonly int[] WaveformPeriodsSec = [1, 30, 300, 3600];
        private static readonly int[] SpikeIntervalsSec = [1, 30, 300, 3600, 36000];
        private static readonly int[] BoolBlinkPeriodsSec = [1, 10, 60, 600, 3600];
        private static readonly int[] BoolSpikeIntervalsSec = [5, 30, 300, 3600, 36000];

        public static readonly IReadOnlyList<TestTypeDef> All = Build();

        private static List<TestTypeDef> Build()
        {
            var list = new List<TestTypeDef> { new(TestSignalKind.Sine, 0) };
            foreach (var kind in new[] { TestSignalKind.Square, TestSignalKind.Triangle, TestSignalKind.Saw })
            {
                list.AddRange(WaveformPeriodsSec.Select(p => new TestTypeDef(kind, p)));
            }

            list.AddRange(SpikeIntervalsSec.Select(p => new TestTypeDef(TestSignalKind.SineSpike, p)));
            list.AddRange(BoolBlinkPeriodsSec.Select(p => new TestTypeDef(TestSignalKind.BoolBlink, p)));
            list.AddRange(BoolSpikeIntervalsSec.Select(p => new TestTypeDef(TestSignalKind.BoolSpike, p)));
            return list;
        }
    }

    internal class TestValue
    {
        private const double Offset = 100;
        private const double Amplitude = 10;
        private const double SpikeAmplitude = 20;

        #region public properties
        public double Value { get => GetValue(); }
        public int Id { get => _dataTag.Id; }
        public int Cycle
        {
            get => _dataTag.Cycle;
            set => _dataTag.Cycle = value;
        }
        #endregion

        #region private fields
        private readonly DataTagModel _dataTag;
        private readonly TestTypeDef _type;
        private readonly Stopwatch _stopwatch;
        private long _lastSpikeEvent;
        #endregion

        #region construction
        public TestValue(DataTagModel dataTag, TestTypeDef type)
        {
            _dataTag = dataTag;
            _type = type;
            _stopwatch = Stopwatch.StartNew();
        }
        #endregion

        #region private methods
        private double GetValue()
        {
            var millis = _stopwatch.ElapsedMilliseconds;
            if (_type.Kind == TestSignalKind.Sine)
            {
                return Math.Sin(millis / 20000.0) + Offset;
            }

            if (_type.Kind == TestSignalKind.SineSpike)
            {
                // One outlier sample per interval, alternating up and down.
                var baseValue = Math.Sin(millis / 20000.0) + Offset;
                var spikeEvent = millis / (_type.PeriodSec * 1000L);
                if (spikeEvent > _lastSpikeEvent)
                {
                    _lastSpikeEvent = spikeEvent;
                    return baseValue + (spikeEvent % 2 == 1 ? SpikeAmplitude : -SpikeAmplitude);
                }

                return baseValue;
            }

            if (_type.Kind == TestSignalKind.BoolSpike)
            {
                // False, except for one true sample per interval.
                var spikeEvent = millis / (_type.PeriodSec * 1000L);
                if (spikeEvent > _lastSpikeEvent)
                {
                    _lastSpikeEvent = spikeEvent;
                    return 1;
                }

                return 0;
            }

            var periodMs = _type.PeriodSec * 1000.0;
            var phase = millis % periodMs / periodMs;
            return _type.Kind switch
            {
                TestSignalKind.BoolBlink => phase < 0.5 ? 1 : 0,
                TestSignalKind.Square => Offset + (phase < 0.5 ? Amplitude : -Amplitude),
                TestSignalKind.Triangle => Offset - Amplitude + 2 * Amplitude * (phase < 0.5 ? 2 * phase : 2 - 2 * phase),
                TestSignalKind.Saw => Offset - Amplitude + 2 * Amplitude * phase,
                _ => Offset,
            };
        }
        #endregion
    }
}
