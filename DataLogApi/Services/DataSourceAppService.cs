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
using DataLogApi.Hosting;
using DataLogLib.Common;
using DataLogLib.Models;
using DataLogService;
using DataLogService.DataSource.OpcUa;
using OpcUaSimpleClientApi;
using System.Security.Cryptography.X509Certificates;

namespace DataLogApi.Services
{
    public class DataSourceAppService
    {
        private const int DefaultTestTagCount = DataSourceModel.DefaultTestTagCount;
        private const int MinTestTagCount = 1;
        private const int MaxTestTagCount = 1000;
        private const int DefaultTestCycleMs = DataSourceModel.DefaultTestCycleMs;
        private static readonly int[] TestCycles = [10, 50, 100, 500, 1000, 5000, 10000];
        private const int ConnectionTestTimeoutMs = 15000;

        private readonly ServiceCtrl _service;

        public DataSourceAppService(ServiceCtrl service)
        {
            _service = service;
        }

        public DashboardDto GetDashboard(ArchiveStatsSnapshot.SnapshotValue stats, DashboardTrendsSnapshot trends)
        {
            var items = GetList();
            var load = _service.GetSystemLoadState();
            var alarms = _service.GetActiveAlarms(null);
            return new DashboardDto
            {
                Total = items.Count,
                Connected = items.Count(i => i.StateId == (int)DataSourceState.Connected),
                Error = items.Count(i => i.StateId == (int)DataSourceState.Error),
                Inactive = items.Count(i => i.StateId == (int)DataSourceState.Inactive),
                ActiveTags = trends.ActiveTags,
                ValuesPerSec = stats.ValuesPerSec,
                ValueCountSumMio = stats.ValueCountSumMio,
                DatabaseDiskSizeGb = stats.DatabaseDiskSizeGb,
                StatsUpdatedAt = stats.StatsUpdatedAt,
                SystemLoadPercent = load.Percent,
                SystemLoadSourceId = load.DataSourceId,
                SystemLoadSourceName = load.DataSourceName,
                SystemLoadTagId = load.DataTagId,
                DatabaseDiskSizeTagId = load.DatabaseDiskSizeTagId,
                ValueCountSumTagId = load.ValueCountSumTagId,
                ValueCountPerSecTagId = load.ValueCountPerSecTagId,
                ValuesPerSecSeries = trends.ValuesPerSecSeries ?? [],
                SystemLoadSeries = trends.LoadPercentSeries ?? [],
                DatabaseGrowthGb7d = stats.DatabaseDiskSizeGb is double now && trends.DatabaseSizeGbWeekAgo is double weekAgo
                    ? now - weekAgo
                    : null,
                PendingAlarms = alarms.Count,
                UnackedAlarms = alarms.Count(a => a.Acked == false),
                IncomingAlarms24h = trends.IncomingAlarms24h,
                Sources = items
            };
        }

        public List<DataSourceListItemDto> GetList()
        {
            return _service.GetDataSourceInfos()
                .Select(info => ToListItem(info))
                .OrderBy(i => i.IsSys ? 0 : 1)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public DataSourceDetailDto GetDetail(int id)
        {
            var model = GetModelOrThrow(id);
            return ToDetail(model);
        }

        public DataSourceDetailDto Create(DataSourceWriteDto dto)
        {
            var type = ParseWritableType(dto.Type);
            ValidateName(dto.Name, excludeId: null);
            ValidateHost(type, dto.Host);

            var model = new DataSourceModel
            {
                Name = dto.Name.Trim(),
                TypeId = (int)type,
                IsActive = dto.IsActive ? 1 : 0,
                IsSys = 0,
                Host = (dto.Host ?? "").Trim(),
                Port = dto.Port > 0 ? dto.Port : DefaultPort(type),
                User = (dto.User ?? "").Trim(),
                Pass = string.IsNullOrWhiteSpace(dto.User) ? "" : dto.Password ?? ""
            };
            if (type == DataSourceType.Test)
            {
                model.TestTagCount = ResolveTestTagCount(dto.TestTagCount);
                model.TestCycleMs = ResolveTestCycleMs(dto.TestCycleMs);
            }
            else if (type == DataSourceType.OpcUa)
            {
                model.OpcUaAlarmsEnabled = dto.OpcUaAlarmsEnabled;
                ApplyOpcUaLocales(model, dto);
                ApplyMinCycle(model, dto);
                ApplyClockWarn(model, dto);
                // New sources are secured; the sources from before 0.0.200 keep None until they are changed.
                model.OpcUaSecurityMode = OpcUaSecurityValues.ModeSignAndEncrypt;
                model.OpcUaSecurityPolicy = OpcUaSecurityValues.PolicyBest;
                model.OpcUaCertTrust = OpcUaSecurityValues.TrustPinned;
                ApplyOpcUaSecurity(model, dto);
            }
            else if (type == DataSourceType.S7CommPlus)
            {
                // S7CommPlus connects without a PLC password (at least HMI access is required).
                model.User = "";
                model.Pass = "";
                ApplyS7Alarms(model, dto);
                ApplyClockWarn(model, dto);
            }

            _service.DataAccess.DataSourceCreate(model);
            return ToDetail(GetModelOrThrow(model.Id));
        }

        public DataSourceDetailDto Update(int id, DataSourceWriteDto dto)
        {
            var existing = GetModelOrThrow(id);
            if (existing.IsSys != 0)
            {
                throw new InvalidOperationException("System data source is read-only.");
            }

            ValidateName(dto.Name, excludeId: id);
            var type = (DataSourceType)existing.TypeId;
            ValidateHost(type, dto.Host);

            existing.Name = dto.Name.Trim();
            existing.IsActive = dto.IsActive ? 1 : 0;
            existing.Host = (dto.Host ?? "").Trim();
            // Same default as Create: an omitted port must not turn an OPC UA source into port 0.
            existing.Port = dto.Port > 0 ? dto.Port : DefaultPort(type);
            existing.User = (dto.User ?? "").Trim();
            if (type == DataSourceType.Test)
            {
                existing.TestTagCount = ResolveTestTagCount(dto.TestTagCount);
                existing.TestCycleMs = ResolveTestCycleMs(dto.TestCycleMs);
            }
            else if (type == DataSourceType.OpcUa)
            {
                existing.OpcUaAlarmsEnabled = dto.OpcUaAlarmsEnabled;
                ApplyOpcUaLocales(existing, dto);
                ApplyMinCycle(existing, dto);
                ApplyClockWarn(existing, dto);
                ApplyOpcUaSecurity(existing, dto);
            }

            if (type == DataSourceType.S7CommPlus)
            {
                existing.User = "";
                existing.Pass = "";
                ApplyS7Alarms(existing, dto);
                ApplyClockWarn(existing, dto);
            }
            else if (existing.User.Length == 0)
            {
                // Without a user the session is anonymous: a stored password would only linger unused.
                existing.Pass = "";
            }
            else if (!string.IsNullOrEmpty(dto.Password))
            {
                existing.Pass = dto.Password;
            }

            _service.DataAccess.DataSourceUpdate(existing);
            return ToDetail(GetModelOrThrow(id));
        }

        /// <summary>
        /// Connects to the OPC UA server with the values of the form (not yet saved) and reads its locales and its min
        /// sample rate. A server that cannot be reached is a result with
        /// <see cref="OpcUaConnectionTestResultDto.Success"/> false.
        /// </summary>
        public OpcUaConnectionTestResultDto TestOpcUaConnection(OpcUaConnectionTestDto dto, CancellationToken cancellationToken)
        {
            ValidateHost(DataSourceType.OpcUa, dto.Host);
            var password = dto.Password ?? "";
            if (string.IsNullOrEmpty(password) && dto.DataSourceId is int id)
            {
                // Same rule as Update: an empty password field keeps the stored password.
                var existing = GetModelOrThrow(id);
                if ((DataSourceType)existing.TypeId == DataSourceType.OpcUa)
                {
                    password = existing.Pass ?? "";
                }
            }

            var port = dto.Port > 0 ? dto.Port : 4840;
            var url = $"opc.tcp://{dto.Host.Trim()}:{port}";
            var security = ResolveTestSecurity(dto);
            var result = OpcUaClientBase.TestConnection(
                url, "DataLogService", (dto.User ?? "").Trim(), password, ConnectionTestTimeoutMs, cancellationToken,
                security);
            return new OpcUaConnectionTestResultDto
            {
                Success = result.Success,
                Error = result.Error ?? "",
                ProductName = result.ProductName ?? "",
                Locales = result.Locales ?? [],
                MinSampleRate = result.MinSampleRate,
                ClockOffsetMs = result.ClockOffsetMs,
                EndpointUrl = result.EndpointUrl ?? "",
                SecurityPolicy = result.SecurityPolicy ?? "",
                SecurityMode = result.SecurityMode ?? "",
                UserIdentity = result.UserIdentity ?? "",
                ApplicationName = result.ApplicationName ?? "",
                ApplicationUri = result.ApplicationUri ?? "",
                ManufacturerName = result.ManufacturerName ?? "",
                SoftwareVersion = result.SoftwareVersion ?? "",
                BuildNumber = result.BuildNumber ?? "",
                ServerState = result.ServerState ?? "",
                StartTimeUtc = result.StartTimeUtc,
                NamespaceCount = result.NamespaceCount,
                MaxNodesPerRead = result.MaxNodesPerRead,
                MaxNodesPerBrowse = result.MaxNodesPerBrowse,
                MaxMonitoredItemsPerCall = result.MaxMonitoredItemsPerCall,
                CurrentSessionCount = result.CurrentSessionCount,
                CurrentSubscriptionCount = result.CurrentSubscriptionCount,
                EventsSupported = result.EventsSupported,
                Endpoints = result.Endpoints ?? [],
                ServerCertificate = result.ServerCertificate,
                CertificateDecision = result.CertificateDecision ?? "",
                ClientNotTrusted = result.ClientNotTrusted,
                SignInRequired = result.SignInRequired
            };
        }

        /// <summary>
        /// The security of a connection test: the values of the form, otherwise those of the edited source, otherwise
        /// the default of a new source (Sign &amp; Encrypt, best policy, pinned certificate). The test checks the
        /// certificate as the source will, so the password never goes to a server that is not trusted.
        /// </summary>
        private OpcUaSecuritySettings ResolveTestSecurity(OpcUaConnectionTestDto dto)
        {
            var model = new DataSourceModel
            {
                OpcUaSecurityMode = OpcUaSecurityValues.ModeSignAndEncrypt,
                OpcUaSecurityPolicy = OpcUaSecurityValues.PolicyBest,
                OpcUaCertTrust = OpcUaSecurityValues.TrustPinned
            };
            if (dto.DataSourceId is int id)
            {
                var existing = GetModelOrThrow(id);
                if ((DataSourceType)existing.TypeId == DataSourceType.OpcUa)
                {
                    model = existing;
                }
            }

            // Mode and policy are validated as on save; the trust is taken apart, since a pin may still be missing.
            var copy = new DataSourceModel
            {
                OpcUaSecurityMode = model.OpcUaSecurityMode,
                OpcUaSecurityPolicy = model.OpcUaSecurityPolicy,
                OpcUaCertTrust = OpcUaSecurityValues.TrustAny
            };
            ApplyOpcUaSecurity(copy, new DataSourceWriteDto
            {
                OpcUaSecurityMode = dto.OpcUaSecurityMode,
                OpcUaSecurityPolicy = dto.OpcUaSecurityPolicy
            });

            var trust = dto.OpcUaCertTrust?.Trim().ToLowerInvariant() ?? model.OpcUaCertTrust;
            if (!OpcUaSecurityValues.Trusts.Contains(trust))
            {
                throw new ArgumentException("Please choose how the server certificate is checked.");
            }

            var thumbprint = dto.OpcUaServerCertThumbprint != null
                ? OpcUaSecurityValues.NormalizeThumbprint(dto.OpcUaServerCertThumbprint)
                : model.OpcUaServerCertThumbprint;
            return new OpcUaSecuritySettings(copy.OpcUaSecurityMode, copy.OpcUaSecurityPolicy, trust, thumbprint);
        }

        /// <summary>
        /// The certificate DataLogXE presents to OPC UA servers, for their trust list.
        /// </summary>
        public X509Certificate2 GetOpcUaClientCertificate()
        {
            return OpcUaClientBase.GetClientCertificate("DataLogService")
                ?? throw new InvalidOperationException("The OPC UA client certificate could not be created.");
        }

        /// <summary>
        /// Connects to the PLC with the values of the form (not yet saved) and reports the CPU, its protection level
        /// and its limits. A PLC that cannot be reached is a result with Success false.
        /// </summary>
        public S7ConnectionTestResultDto TestS7CommPlusConnection(OpcUaConnectionTestDto dto)
        {
            ValidateHost(DataSourceType.S7CommPlus, dto.Host);
            var port = dto.Port > 0 ? dto.Port : DefaultPort(DataSourceType.S7CommPlus);
            var result = _service.TestS7CommPlusConnection(dto.Host.Trim(), port, ConnectionTestTimeoutMs - 3000);
            return new S7ConnectionTestResultDto
            {
                Success = result.Success,
                Error = result.Error ?? "",
                CpuFamily = result.CpuFamily ?? "",
                OrderNumber = result.OrderNumber ?? "",
                Firmware = result.Firmware ?? "",
                ProtectionLevel = result.ProtectionLevel,
                SubscriptionsMax = result.SubscriptionsMax,
                SubscriptionsFree = result.SubscriptionsFree,
                AttributesMax = result.AttributesMax,
                AttributesFree = result.AttributesFree,
                DataBlockCount = result.DataBlockCount,
                DataLogXeSources = result.DataLogXeSources,
                DataLogXeSubscriptions = result.DataLogXeSubscriptions,
                DataLogXeValues = result.DataLogXeValues,
                AlarmsSupported = result.AlarmsSupported,
                ActiveAlarmCount = result.ActiveAlarmCount,
                ClockOffsetMs = result.ClockOffsetMs
            };
        }

        private static int DefaultPort(DataSourceType type)
        {
            return type switch
            {
                DataSourceType.OpcUa => 4840,
                DataSourceType.S7CommPlus => 102,
                _ => 0
            };
        }

        public void Delete(int id)
        {
            var existing = GetModelOrThrow(id);
            if (existing.IsSys != 0)
            {
                throw new InvalidOperationException("System data source cannot be deleted.");
            }

            _service.DeleteDataSource(id);
        }

        private DataSourceModel GetModelOrThrow(int id)
        {
            try
            {
                return _service.DataAccess.DataSourceGetById(id);
            }
            catch (InvalidOperationException)
            {
                throw new KeyNotFoundException($"Data source {id} was not found.");
            }
        }

        private void ValidateName(string name, int? excludeId)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Please enter a valid data source name.");
            }

            var trimmed = name.Trim().ToUpperInvariant();
            var sources = _service.DataAccess.DataSourceGetAll();
            var clash = sources.FirstOrDefault(ds =>
                (ds.Name ?? "").Trim().ToUpperInvariant() == trimmed &&
                (!excludeId.HasValue || ds.Id != excludeId.Value));

            if (clash != null)
            {
                throw new InvalidOperationException("A data source with this name already exists.");
            }
        }

        private static void ValidateHost(DataSourceType type, string host)
        {
            if ((type == DataSourceType.OpcUa || type == DataSourceType.S7CommPlus) && string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Please enter a valid IP address.");
            }
        }

        private static void ApplyMinCycle(DataSourceModel model, DataSourceWriteDto dto)
        {
            // The rate comes from the server (connection test or connect); omitted keeps the stored value.
            if (dto.MinCycle is int minCycle)
            {
                model.MinCycle = Math.Clamp(minCycle, 0, 10000);
            }
        }

        private static bool HasClock(DataSourceModel ds)
        {
            var type = (DataSourceType)ds.TypeId;
            return type == DataSourceType.OpcUa || type == DataSourceType.S7CommPlus;
        }

        private static void ApplyClockWarn(DataSourceModel model, DataSourceWriteDto dto)
        {
            if (dto.ClockWarnMs is int clockWarnMs)
            {
                if (clockWarnMs <= 0)
                {
                    // An older client turned the watch off with a threshold of 0.
                    model.ClockMonitor = false;
                }
                else
                {
                    model.ClockWarnMs = Math.Min(clockWarnMs, 86_400_000);
                }
            }
            if (dto.ClockMonitor is bool clockMonitor)
            {
                model.ClockMonitor = clockMonitor;
            }
            if (dto.ClockCompensate is bool clockCompensate)
            {
                model.ClockCompensate = clockCompensate;
            }
        }

        private static void ApplyS7Alarms(DataSourceModel model, DataSourceWriteDto dto)
        {
            model.S7AlarmsEnabled = dto.S7AlarmsEnabled;
            if (dto.S7Locale == null)
            {
                return;
            }

            var locale = S7AlarmLanguages.Normalize(dto.S7Locale)
                ?? throw new ArgumentException($"Unknown alarm text language '{dto.S7Locale.Trim()}'.");
            model.S7Locale = locale;
        }

        /// <summary>
        /// Takes the OPC UA security of the form; omitted fields keep the values of the model. A pinned certificate
        /// needs a thumbprint, which the form takes from a connection test.
        /// </summary>
        internal static void ApplyOpcUaSecurity(DataSourceModel model, DataSourceWriteDto dto)
        {
            if (dto.OpcUaSecurityMode != null)
            {
                var mode = dto.OpcUaSecurityMode.Trim().ToLowerInvariant();
                if (!OpcUaSecurityValues.Modes.Contains(mode))
                {
                    throw new ArgumentException("Please choose a valid security mode.");
                }

                model.OpcUaSecurityMode = mode;
            }

            if (dto.OpcUaSecurityPolicy != null)
            {
                var policy = dto.OpcUaSecurityPolicy.Trim();
                if (policy.Equals(OpcUaSecurityValues.PolicyBest, StringComparison.OrdinalIgnoreCase))
                {
                    policy = OpcUaSecurityValues.PolicyBest;
                }
                else if (!OpcUaSecurityValues.Policies.Contains(policy))
                {
                    throw new ArgumentException("Please choose a valid security policy.");
                }

                model.OpcUaSecurityPolicy = policy;
            }

            if (dto.OpcUaCertTrust != null)
            {
                var trust = dto.OpcUaCertTrust.Trim().ToLowerInvariant();
                if (!OpcUaSecurityValues.Trusts.Contains(trust))
                {
                    throw new ArgumentException("Please choose how the server certificate is checked.");
                }

                model.OpcUaCertTrust = trust;
            }

            if (dto.OpcUaServerCertThumbprint != null)
            {
                var thumbprint = OpcUaSecurityValues.NormalizeThumbprint(dto.OpcUaServerCertThumbprint);
                if (thumbprint.Length != 0 && thumbprint.Length != 40)
                {
                    throw new ArgumentException("The certificate thumbprint must have 40 hex digits.");
                }

                model.OpcUaServerCertThumbprint = thumbprint;
                // Name and validity belong to this certificate; they are only shown in the form
                model.OpcUaServerCertSubject = thumbprint.Length == 0 ? "" : (dto.OpcUaServerCertSubject ?? "").Trim();
                model.OpcUaServerCertNotAfter = thumbprint.Length == 0 ? null : dto.OpcUaServerCertNotAfterUtc?.ToUniversalTime();
            }

            if (model.OpcUaSecurityMode != OpcUaSecurityValues.ModeNone
                && model.OpcUaCertTrust == OpcUaSecurityValues.TrustPinned
                && string.IsNullOrEmpty(model.OpcUaServerCertThumbprint))
            {
                throw new ArgumentException(
                    "Run the connection test and take over the server certificate before saving, " +
                    "or choose to accept any server certificate.");
            }
        }

        private static void ApplyOpcUaLocales(DataSourceModel model, DataSourceWriteDto dto)
        {
            // Omitted fields keep the stored values, so older clients do not clear them.
            if (dto.OpcUaAvailableLocales != null)
            {
                model.OpcUaAvailableLocales = dto.OpcUaAvailableLocales
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .Where(l => !l.Contains(','))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (dto.OpcUaLocale != null)
            {
                // A locale missing from the list is accepted: not every server reports its locales.
                var locale = dto.OpcUaLocale.Trim();
                if (locale.Length > 64)
                {
                    throw new ArgumentException("Please choose a valid locale.");
                }

                model.OpcUaLocale = locale;
            }
        }

        private static int ResolveTestTagCount(int count)
        {
            var resolved = count <= 0 ? DefaultTestTagCount : count;
            if (resolved < MinTestTagCount || resolved > MaxTestTagCount)
            {
                throw new ArgumentException("Please enter a valid test tag count per type (1-1000).");
            }

            return resolved;
        }

        private static int ResolveTestCycleMs(int cycleMs)
        {
            var resolved = cycleMs <= 0 ? DefaultTestCycleMs : cycleMs;
            if (!TestCycles.Contains(resolved))
            {
                throw new ArgumentException("Please choose a valid cycle.");
            }

            return resolved;
        }

        private static int ReadTestTagCount(DataSourceModel ds)
        {
            if ((DataSourceType)ds.TypeId != DataSourceType.Test)
            {
                return 0;
            }

            var count = ds.TestTagCount;
            return count > 0 ? count : DefaultTestTagCount;
        }

        private static int ReadTestCycleMs(DataSourceModel ds)
        {
            if ((DataSourceType)ds.TypeId != DataSourceType.Test)
            {
                return 0;
            }

            var cycleMs = ds.TestCycleMs;
            return TestCycles.Contains(cycleMs) ? cycleMs : DefaultTestCycleMs;
        }

        private static DataSourceType ParseWritableType(string type)
        {
            if (string.Equals(type, nameof(DataSourceType.OpcUa), StringComparison.OrdinalIgnoreCase) ||
                type == "2")
            {
                return DataSourceType.OpcUa;
            }

            if (string.Equals(type, nameof(DataSourceType.S7CommPlus), StringComparison.OrdinalIgnoreCase) ||
                type == "3")
            {
                return DataSourceType.S7CommPlus;
            }

            if (string.Equals(type, nameof(DataSourceType.Test), StringComparison.OrdinalIgnoreCase) ||
                type == "99")
            {
                return DataSourceType.Test;
            }

            throw new ArgumentException("Please enter a valid data source type.");
        }

        private DataSourceListItemDto ToListItem(DataSourceInfoModel info)
        {
            var ds = info.DataSource;
            var ui = _service.GetDataSourceListUiState(ds.Id);
            return new DataSourceListItemDto
            {
                Id = ds.Id,
                Name = ds.Name,
                Type = ((DataSourceType)ds.TypeId).ToString(),
                TypeId = ds.TypeId,
                State = info.State.ToString(),
                StateId = (int)info.State,
                Host = ds.Host ?? "",
                Port = ds.Port,
                IsActive = ds.IsActive != 0,
                IsSys = ds.IsSys != 0,
                OpcUaAlarmsEnabled = (DataSourceType)ds.TypeId == DataSourceType.OpcUa
                    && ds.OpcUaAlarmsEnabled,
                S7AlarmsEnabled = (DataSourceType)ds.TypeId == DataSourceType.S7CommPlus
                    && ds.S7AlarmsEnabled,
                ClockOffsetMs = info.ClockOffsetMs,
                // 0 hides the warning when the clock is not watched
                ClockWarnMs = HasClock(ds) && ds.ClockMonitor ? ds.ClockWarnMs : 0,
                OpcUaSecurityMode = IsOpcUa(ds) ? ds.OpcUaSecurityMode ?? OpcUaSecurityValues.ModeNone : "",
                OpcUaCertificateRejected = !string.IsNullOrEmpty(info.RejectedCertificateThumbprint),
                CreatedAt = ds.CreatedAt,
                TagBrowseActive = ui.TagBrowseActive,
                TagImportActive = ui.TagImportActive,
                ValuesPerSec = ui.ValuesPerSec,
                ValuesPerSecSeries = ui.ValuesPerSecSeries?.ToList() ?? []
            };
        }

        private static DataSourceDetailDto ToDetail(DataSourceModel ds)
        {
            return new DataSourceDetailDto
            {
                Id = ds.Id,
                Name = ds.Name,
                Type = ((DataSourceType)ds.TypeId).ToString(),
                TypeId = ds.TypeId,
                IsActive = ds.IsActive != 0,
                IsSys = ds.IsSys != 0,
                Host = ds.Host ?? "",
                Port = ds.Port,
                User = ds.User ?? "",
                HasPassword = !string.IsNullOrEmpty(ds.Pass),
                MinCycle = ds.MinCycle,
                TestTagCount = ReadTestTagCount(ds),
                TestCycleMs = ReadTestCycleMs(ds),
                OpcUaAlarmsEnabled = ReadOpcUaAlarmsEnabled(ds),
                OpcUaLocale = (DataSourceType)ds.TypeId == DataSourceType.OpcUa ? ds.OpcUaLocale ?? "" : "",
                OpcUaAvailableLocales = (DataSourceType)ds.TypeId == DataSourceType.OpcUa
                    ? ds.OpcUaAvailableLocales ?? []
                    : [],
                S7AlarmsEnabled = (DataSourceType)ds.TypeId == DataSourceType.S7CommPlus && ds.S7AlarmsEnabled,
                S7Locale = (DataSourceType)ds.TypeId == DataSourceType.S7CommPlus ? ds.S7Locale ?? "" : "",
                ClockWarnMs = HasClock(ds) ? ds.ClockWarnMs : 0,
                ClockMonitor = HasClock(ds) && ds.ClockMonitor,
                ClockCompensate = HasClock(ds) && ds.ClockCompensate,
                OpcUaSecurityMode = IsOpcUa(ds) ? ds.OpcUaSecurityMode ?? OpcUaSecurityValues.ModeNone : "",
                OpcUaSecurityPolicy = IsOpcUa(ds) ? ds.OpcUaSecurityPolicy ?? OpcUaSecurityValues.PolicyBest : "",
                OpcUaCertTrust = IsOpcUa(ds) ? ds.OpcUaCertTrust ?? OpcUaSecurityValues.TrustAny : "",
                OpcUaServerCertThumbprint = IsOpcUa(ds) ? ds.OpcUaServerCertThumbprint ?? "" : "",
                OpcUaServerCertSubject = IsOpcUa(ds) ? ds.OpcUaServerCertSubject ?? "" : "",
                OpcUaServerCertNotAfterUtc = IsOpcUa(ds) ? ds.OpcUaServerCertNotAfter : null,
                CreatedAt = ds.CreatedAt
            };
        }

        private static bool IsOpcUa(DataSourceModel ds)
        {
            return (DataSourceType)ds.TypeId == DataSourceType.OpcUa;
        }

        private static bool ReadOpcUaAlarmsEnabled(DataSourceModel ds)
        {
            if ((DataSourceType)ds.TypeId != DataSourceType.OpcUa)
            {
                return true;
            }

            return ds.OpcUaAlarmsEnabled;
        }
    }
}
