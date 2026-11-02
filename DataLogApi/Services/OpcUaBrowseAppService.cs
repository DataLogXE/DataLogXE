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
using DataLogService;

namespace DataLogApi.Services
{
    public class OpcUaBrowseAppService
    {
        private readonly ServiceCtrl _service;

        public OpcUaBrowseAppService(ServiceCtrl service)
        {
            _service = service;
        }

        public OpcUaBrowseStatusDto Start(int dataSourceId)
        {
            EnsureSourceExists(dataSourceId);
            _service.StartOpcUaBrowse(dataSourceId);
            return ToDto(_service.GetOpcUaBrowseStatus(dataSourceId));
        }

        public OpcUaBrowseStatusDto Get(int dataSourceId)
        {
            EnsureSourceExists(dataSourceId);
            return ToDto(_service.GetOpcUaBrowseStatus(dataSourceId));
        }

        public void Cancel(int dataSourceId)
        {
            EnsureSourceExists(dataSourceId);
            _service.CancelOpcUaBrowse(dataSourceId);
        }

        private void EnsureSourceExists(int dataSourceId)
        {
            try
            {
                _service.DataAccess.DataSourceGetById(dataSourceId);
            }
            catch (InvalidOperationException)
            {
                throw new KeyNotFoundException($"Data source {dataSourceId} was not found.");
            }
        }

        private static OpcUaBrowseStatusDto ToDto(OpcUaBrowseJobStatus status)
        {
            status ??= new OpcUaBrowseJobStatus();
            return new OpcUaBrowseStatusDto
            {
                Running = status.Running,
                Paused = status.Paused,
                Completed = status.Completed,
                Error = status.Error,
                Lines = (status.Lines ?? []).Select(line => new OpcUaBrowseStatusLineDto
                {
                    Text = line.Text ?? "",
                    ReplaceLast = line.ReplaceLast
                }).ToList()
            };
        }
    }
}
