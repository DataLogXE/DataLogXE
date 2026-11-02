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

namespace DataLogApi.Services
{
    public class TelegrafConfigAppService
    {
        private readonly string _outputAddress;
        private readonly string _interval;

        public TelegrafConfigAppService(IConfiguration configuration)
        {
            var port = configuration["TELEGRAF_PORT"] ?? "8092";
            _outputAddress = $"tcp://datalogxe:{port}";
            _interval = $"{Consts.TELEGRAF_INTERVAL_MS / 1000}s";
        }

        public string Build()
        {
            return $"""
                # Telegraf -> DataLogXE System data source (TCP Influx line protocol)
                [agent]
                  interval = "{_interval}"
                  round_interval = true
                  metric_batch_size = 1000
                  metric_buffer_limit = 10000
                  collection_jitter = "0s"
                  flush_interval = "10s"
                  flush_jitter = "0s"
                  precision = "0s"
                  omit_hostname = false

                [[outputs.socket_writer]]
                  address = "{_outputAddress}"
                  data_format = "influx"

                [[inputs.cpu]]
                  percpu = false
                  totalcpu = true
                  collect_cpu_time = false
                  report_active = false
                  core_tags = false

                [[inputs.disk]]
                  ignore_fs = ["tmpfs", "devtmpfs", "devfs", "iso9660", "overlay", "aufs", "squashfs"]

                [[inputs.kernel]]

                [[inputs.mem]]

                [[inputs.swap]]

                [[inputs.system]]

                """;
        }
    }
}
