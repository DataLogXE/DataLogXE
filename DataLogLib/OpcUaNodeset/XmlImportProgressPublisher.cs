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

using System.Globalization;
using System.Text;

namespace DataLogLib.OpcUaNodeset
{
    internal sealed class XmlImportProgressPublisher : IDisposable
    {
        private readonly string _path;
        private readonly XmlImportCounters _counters;
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly Thread _thread;
        private string _phase = "parse";
        private string _error = "";

        public XmlImportProgressPublisher(string path, XmlImportCounters counters)
        {
            _path = path;
            _counters = counters;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "XmlImportStatus"
            };
        }

        public void Start()
        {
            Flush();
            _thread.Start();
        }

        public void SetPhase(string phase)
        {
            lock (_gate)
            {
                _phase = phase ?? "";
            }

            Flush();
        }

        public void Fail(string error)
        {
            lock (_gate)
            {
                _phase = "failed";
                _error = error ?? "";
            }

            Flush();
        }

        public void Flush()
        {
            string phase;
            string error;
            lock (_gate)
            {
                phase = _phase;
                error = _error;
            }

            var builder = new StringBuilder(384);
            builder.Append("phase=").Append(phase).Append('\n');
            builder.Append("bytesRead=").Append(_counters.BytesRead.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("fileBytes=").Append(_counters.FileBytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("variablesRead=").Append(_counters.VariablesRead.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("itemsAccepted=").Append(_counters.ItemsAccepted.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("waiting=").Append(_counters.WaitingForWriter ? "1" : "0").Append('\n');
            builder.Append("slowed=").Append(_counters.Slowed ? "1" : "0").Append('\n');
            builder.Append("work=").Append(_counters.WorkTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("done=").Append(_counters.PhaseDone.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("total=").Append(_counters.PhaseTotal.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("aliveUtc=").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("error=").Append(error.Replace('\n', ' ')).Append('\n');
            var temp = _path + ".tmp";
            lock (_gate)
            {
                File.WriteAllText(temp, builder.ToString());
                File.Move(temp, _path, true);
            }
        }

        public void Dispose()
        {
            _stop.Set();
            if (_thread.IsAlive)
            {
                _thread.Join(1000);
            }

            Flush();
            _stop.Dispose();
        }

        private void Loop()
        {
            while (!_stop.Wait(400))
            {
                try
                {
                    Flush();
                }
                catch (Exception)
                {
                    // The parent reports a stall when work ticks stop moving.
                }
            }
        }
    }
}
