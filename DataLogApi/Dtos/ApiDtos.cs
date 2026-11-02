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

using System.Text.Json.Serialization;

namespace DataLogApi.Dtos
{
    public class ErrorDto
    {
        public string Error { get; set; }
    }

    public class DashboardDto
    {
        public int Total { get; set; }
        public int Connected { get; set; }
        public int Error { get; set; }
        public int Inactive { get; set; }
        public int? ActiveTags { get; set; }
        public long? ValuesPerSec { get; set; }
        public double? ValueCountSumMio { get; set; }
        public double? DatabaseDiskSizeGb { get; set; }
        public DateTime? StatsUpdatedAt { get; set; }
        public double? SystemLoadPercent { get; set; }
        public int? SystemLoadSourceId { get; set; }
        public string SystemLoadSourceName { get; set; }
        public int? SystemLoadTagId { get; set; }
        public int? DatabaseDiskSizeTagId { get; set; }
        public int? ValueCountSumTagId { get; set; }
        public int? ValueCountPerSecTagId { get; set; }
        // Samples of the last 10 minutes, one per 10 s, oldest first
        public double[] ValuesPerSecSeries { get; set; } = [];
        public double[] SystemLoadSeries { get; set; } = [];
        // Database size now minus a week ago, null while the archive is younger than a week
        public double? DatabaseGrowthGb7d { get; set; }
        public int PendingAlarms { get; set; }
        public int UnackedAlarms { get; set; }
        // Alarms that came in the last 24 hours, null when the count failed
        public long? IncomingAlarms24h { get; set; }
        public List<DataSourceListItemDto> Sources { get; set; } = [];
    }

    public class DataSourceListItemDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public int TypeId { get; set; }
        public string State { get; set; }
        public int StateId { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public bool IsActive { get; set; }
        public bool IsSys { get; set; }
        public bool OpcUaAlarmsEnabled { get; set; }
        public bool S7AlarmsEnabled { get; set; }

        /// <summary>OPC UA: the message security (none, sign, signandencrypt); empty for other types.</summary>
        public string OpcUaSecurityMode { get; set; } = "";

        /// <summary>OPC UA: the last connect failed because the server showed another than the pinned certificate.</summary>
        public bool OpcUaCertificateRejected { get; set; }

        /// <summary>Device clock minus DataLogXE clock in ms while connected (positive: the device is ahead), null when unknown.</summary>
        public long? ClockOffsetMs { get; set; }

        /// <summary>Clock offset above which the list warns, 0 = no warning (also when the clock is not monitored).</summary>
        public int ClockWarnMs { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool TagBrowseActive { get; set; }
        public bool TagImportActive { get; set; }
        public double? ValuesPerSec { get; set; }
        public List<double> ValuesPerSecSeries { get; set; } = [];
    }

    public class DataSourceDetailDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public int TypeId { get; set; }
        public bool IsActive { get; set; }
        public bool IsSys { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public bool HasPassword { get; set; }
        public int MinCycle { get; set; }
        public int TestTagCount { get; set; }
        public int TestCycleMs { get; set; }
        public bool OpcUaAlarmsEnabled { get; set; } = true;
        public string OpcUaLocale { get; set; } = "";
        public List<string> OpcUaAvailableLocales { get; set; } = [];
        public bool S7AlarmsEnabled { get; set; }
        public string S7Locale { get; set; } = "";
        public int ClockWarnMs { get; set; }
        public bool ClockMonitor { get; set; }
        public bool ClockCompensate { get; set; }
        public string OpcUaSecurityMode { get; set; } = "";
        public string OpcUaSecurityPolicy { get; set; } = "";
        public string OpcUaCertTrust { get; set; } = "";
        public string OpcUaServerCertThumbprint { get; set; } = "";
        public string OpcUaServerCertSubject { get; set; } = "";
        public DateTime? OpcUaServerCertNotAfterUtc { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class DataSourceWriteDto
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool IsActive { get; set; } = true;
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }

        /// <summary>
        /// The min sample rate a connection test read from the server; null keeps the stored value.
        /// </summary>
        public int? MinCycle { get; set; }
        public int TestTagCount { get; set; }
        public int TestCycleMs { get; set; }
        public bool OpcUaAlarmsEnabled { get; set; } = true;
        public string OpcUaLocale { get; set; }
        public List<string> OpcUaAvailableLocales { get; set; }
        public bool S7AlarmsEnabled { get; set; }

        /// <summary>Language of the S7 alarm texts (e.g. "de-DE"); null keeps the stored value, empty is English.</summary>
        public string S7Locale { get; set; }

        /// <summary>Clock offset above which the clock watch warns; null keeps the stored value.</summary>
        public int? ClockWarnMs { get; set; }

        /// <summary>Whether the device clock is watched; null keeps the stored value.</summary>
        public bool? ClockMonitor { get; set; }

        /// <summary>Whether value and alarm times are moved by the clock difference; null keeps the stored value.</summary>
        public bool? ClockCompensate { get; set; }

        /// <summary>OPC UA message security: none, sign, signandencrypt; null keeps the stored value (new: signandencrypt).</summary>
        public string OpcUaSecurityMode { get; set; }

        /// <summary>OPC UA security policy: best or a policy name; null keeps the stored value (new: best).</summary>
        public string OpcUaSecurityPolicy { get; set; }

        /// <summary>OPC UA server certificate check: any or pinned; null keeps the stored value (new: pinned).</summary>
        public string OpcUaCertTrust { get; set; }

        /// <summary>Thumbprint of the pinned OPC UA server certificate; null keeps the stored value.</summary>
        public string OpcUaServerCertThumbprint { get; set; }

        /// <summary>Subject of the pinned certificate, taken with a thumbprint (else ignored).</summary>
        public string OpcUaServerCertSubject { get; set; }

        /// <summary>End of the validity of the pinned certificate, taken with a thumbprint (else ignored).</summary>
        public DateTime? OpcUaServerCertNotAfterUtc { get; set; }
    }

    public class OpcUaConnectionTestDto
    {
        /// <summary>
        /// The data source being edited, null for a new one; its stored password is used when Password is empty.
        /// </summary>
        public int? DataSourceId { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }

        /// <summary>OPC UA message security to test with; null takes the stored value, else signandencrypt.</summary>
        public string OpcUaSecurityMode { get; set; }

        /// <summary>OPC UA security policy to test with; null takes the stored value, else best.</summary>
        public string OpcUaSecurityPolicy { get; set; }

        /// <summary>Server certificate check to test with, any or pinned; null takes the stored value, else pinned.</summary>
        public string OpcUaCertTrust { get; set; }

        /// <summary>The thumbprint the server certificate is compared with; null takes the stored one.</summary>
        public string OpcUaServerCertThumbprint { get; set; }
    }

    public class S7ConnectionTestResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string CpuFamily { get; set; } = "";
        public string OrderNumber { get; set; } = "";
        public string Firmware { get; set; } = "";
        /// <summary>1 full access, 2 read access, 3 HMI access, 4 no access (without password).</summary>
        public int ProtectionLevel { get; set; }
        public int SubscriptionsMax { get; set; }
        public int SubscriptionsFree { get; set; }
        /// <summary>Each subscribed value uses one attribute of the CPU, shared by all clients (HMI, SCADA, ...).</summary>
        public int AttributesMax { get; set; }
        public int AttributesFree { get; set; }
        public int DataBlockCount { get; set; }
        /// <summary>Running DataLogXE sources on this PLC and the subscriptions / values they hold.</summary>
        public int DataLogXeSources { get; set; }
        public int DataLogXeSubscriptions { get; set; }
        public int DataLogXeValues { get; set; }
        public bool AlarmsSupported { get; set; }
        public int ActiveAlarmCount { get; set; } = -1;
        public long? ClockOffsetMs { get; set; }
    }

    public class OpcUaConnectionTestResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string ProductName { get; set; } = "";
        public List<string> Locales { get; set; } = [];
        public int? MinSampleRate { get; set; }
        public long? ClockOffsetMs { get; set; }
        public string EndpointUrl { get; set; } = "";
        public string SecurityPolicy { get; set; } = "";
        public string SecurityMode { get; set; } = "";
        public string UserIdentity { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string ApplicationUri { get; set; } = "";
        public string ManufacturerName { get; set; } = "";
        public string SoftwareVersion { get; set; } = "";
        public string BuildNumber { get; set; } = "";
        public string ServerState { get; set; } = "";
        public DateTime? StartTimeUtc { get; set; }
        public int? NamespaceCount { get; set; }
        public long? MaxNodesPerRead { get; set; }
        public long? MaxNodesPerBrowse { get; set; }
        public long? MaxMonitoredItemsPerCall { get; set; }
        public long? CurrentSessionCount { get; set; }
        public long? CurrentSubscriptionCount { get; set; }
        public bool? EventsSupported { get; set; }
        public List<DataLogService.DataSource.OpcUa.OpcUaEndpointInfo> Endpoints { get; set; } = [];
        public DataLogService.DataSource.OpcUa.OpcUaCertificateInfo ServerCertificate { get; set; }

        /// <summary>
        /// What the user must decide before the test signs in, see <see cref="DataLogService.DataSource.OpcUa.OpcUaCertificateDecision"/>;
        /// empty when nothing.
        /// </summary>
        public string CertificateDecision { get; set; } = "";

        /// <summary>The server refused the secure channel: it does not trust the client certificate of DataLogXE yet.</summary>
        public bool ClientNotTrusted { get; set; }

        /// <summary>The server does not allow an anonymous session: the source needs a user name and password.</summary>
        public bool SignInRequired { get; set; }
    }

    public class OpcUaClientCertificateDto
    {
        public DataLogService.DataSource.OpcUa.OpcUaCertificateInfo Certificate { get; set; }
    }

    public class DataTagListItemDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Address { get; set; }
        /// <summary>Comment from the source (S7: PLC comment) in the language of the source, empty when not translated.</summary>
        public string Comment { get; set; }
        public int Cycle { get; set; }
        public bool IsActive { get; set; }
        public bool IsSys { get; set; }
    }

    public class DataTagListPageDto
    {
        public int DataSourceId { get; set; }
        public string DataSourceName { get; set; }
        public string DataSourceState { get; set; }
        public int DataSourceStateId { get; set; }
        public List<int> Cycles { get; set; } = [];
        public string DataSourceType { get; set; }
        public int Total { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }
        public bool Preparing { get; set; }
        public int PreparePercent { get; set; }
        public List<DataTagListItemDto> Items { get; set; } = [];
    }

    public class DataTagIdsRequestDto
    {
        public List<int> Ids { get; set; } = [];
    }

    public class DataTagActualValueDto
    {
        public int Id { get; set; }
        public double Value { get; set; }
        public DateTime Time { get; set; }
        public string State { get; set; }
    }

    public class DataTagIssueDto
    {
        public int Id { get; set; }
        public string Issue { get; set; }
    }

    public class DataTagActualValuesDto
    {
        public string DataSourceState { get; set; }
        public int DataSourceStateId { get; set; }
        public List<DataTagActualValueDto> Values { get; set; } = [];
        /// <summary>Problems of single tags reported by the running source (S7CommPlus: not resolved, PLC limit ...).</summary>
        public List<DataTagIssueDto> Issues { get; set; } = [];
    }

    public class DataTagActiveWriteDto
    {
        public List<int> Ids { get; set; } = [];
        public bool IsActive { get; set; }
    }

    public class DataTagCycleWriteDto
    {
        public List<int> Ids { get; set; } = [];
        public int Cycle { get; set; }
    }

    /// <summary>Active and cycle for all ids; a null value is left as it is.</summary>
    public class DataTagUpdateDto
    {
        public List<int> Ids { get; set; } = [];
        public bool? IsActive { get; set; }
        public int? Cycle { get; set; }
    }

    public class DataTagDetailsDto
    {
        public int Id { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Address { get; set; }
        /// <summary>Comment from the source (S7: PLC comment) in the language of the source, empty when not translated.</summary>
        public string Comment { get; set; }
        /// <summary>Bool, Number or Unknown.</summary>
        public string Type { get; set; }
        public int Cycle { get; set; }
        public bool IsActive { get; set; }
        public bool IsSys { get; set; }
        public DateTime? CreatedAt { get; set; }
    }

    public class DataTagDetailsListDto
    {
        public List<int> Cycles { get; set; } = [];
        public List<DataTagDetailsDto> Items { get; set; } = [];
    }

    public class DataTagMutationResultDto
    {
        public List<DataTagListItemDto> Updated { get; set; } = [];
        // Selected tags left inactive because they cannot be logged (strings and system types of an S7 source)
        public int NotActivated { get; set; }
    }

    public class PlotPointsDto
    {
        public int DataTagId { get; set; }
        public string Name { get; set; }
        public bool IsBool { get; set; }
        public string Compression { get; set; } = "RAW";
        public int CompressionSeconds { get; set; }
        // Pixel width in seconds when the points were reduced to the M4 points per pixel, else 0
        public double ReductionSeconds { get; set; }
        public double? ArchiveStart { get; set; }
        public List<double> Times { get; set; } = [];
        public List<double?> Raw { get; set; } = [];
    }

    /// <summary>The raw values of a data tag between two times for an export, at most Limit of them.</summary>
    public class RawValuesDto
    {
        public int DataTagId { get; set; }
        public string Name { get; set; }
        public bool IsBool { get; set; }
        // True when the range holds more rows than the limit and only the first ones came
        public bool Truncated { get; set; }
        public List<double> Times { get; set; } = [];
        public List<double?> Raw { get; set; } = [];
    }

    /// <summary>The number of raw values of a data tag between two times, from the counters of the archive.</summary>
    public class RawValueCountDto
    {
        public int DataTagId { get; set; }
        public long? Count { get; set; }
    }

    /// <summary>The statistics of the raw values of a data tag between two times, as the archive holds them.</summary>
    public class DataTagStatisticsDto
    {
        public int DataTagId { get; set; }
        public string Name { get; set; }
        public bool IsBool { get; set; }
        /// <summary>The range in unix seconds.</summary>
        public double From { get; set; }
        public double To { get; set; }
        /// <summary>The valid raw values in the range; NaN and invalid values are not counted.</summary>
        public long Count { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Mean { get; set; }
        /// <summary>Sample standard deviation, null with fewer than two values.</summary>
        public double? StdDev { get; set; }
        /// <summary>For a bool tag how long it was high and low in the range, else null.</summary>
        public double? HighSeconds { get; set; }
        public double? LowSeconds { get; set; }
        /// <summary>For a bool tag how often it changed between 0 and 1 in the range, else null.</summary>
        public long? SignalChanges { get; set; }
        /// <summary>For a bool tag the longest and the shortest high and low time in the range, else null. The shortest are only taken from the times that began and ended with a change in the range.</summary>
        public double? LongestHighSeconds { get; set; }
        public double? ShortestHighSeconds { get; set; }
        public double? LongestLowSeconds { get; set; }
        public double? ShortestLowSeconds { get; set; }
        /// <summary>How long the tag had no signal in the range: NaN or invalid, the time before an invalid value, and the time after its last value once the hold time of the tag has passed (a source that stopped delivering).</summary>
        public double? InvalidSeconds { get; set; }
    }

    /// <summary>The raw value of a data tag at a time, as the archive holds it.</summary>
    public class RawValueAtDto
    {
        public int DataTagId { get; set; }
        /// <summary>Time of the value in unix seconds, null when the archive has none before the time.</summary>
        public double? Time { get; set; }
        /// <summary>Null when there is no value, or it is NaN or invalid.</summary>
        public double? Value { get; set; }
    }

    public class OpcUaBrowseStatusDto
    {
        public bool Running { get; set; }
        public bool Paused { get; set; }
        public bool Completed { get; set; }
        public string Error { get; set; }
        public List<OpcUaBrowseStatusLineDto> Lines { get; set; } = [];
    }

    public class OpcUaBrowseStatusLineDto
    {
        public string Text { get; set; }
        public bool ReplaceLast { get; set; }
        public double? Percent { get; set; }
    }

    public class EventLogItemDto
    {
        public DateTime Time { get; set; }
        public string Level { get; set; }
        public string Kind { get; set; }
        public string Component { get; set; }
        public int SourceId { get; set; }
        public string SourceType { get; set; }
        public string Message { get; set; }
        public string Template { get; set; }
        public string Exception { get; set; }
        public string Props { get; set; }
        public string CorrelationId { get; set; }
        public string AlarmState { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Severity { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Active { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Acked { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Retain { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? ActiveSince { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string EventType { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string EventTypeName { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AlarmClassSymbol { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AlarmClassName { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint? TextColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint? BackColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Flashing { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? DurationMs { get; set; }
    }

    public class EventLogPageDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int Limit { get; set; }
        public bool HasMore { get; set; }
        public string NextCursor { get; set; }
        // Sorted alarms the table can scroll; null for the storage order.
        public long? Total { get; set; }
        // Alarms in a sorted range; larger than Total when only the first ones of the order are sorted.
        public long? SortedOf { get; set; }
        public List<EventLogItemDto> Items { get; set; } = [];
    }

    /// <summary>
    /// State of a background query. Result is only set once the job is done; Error and ErrorStatus
    /// only once it failed.
    /// </summary>
    public class QueryJobStatusDto
    {
        public string Id { get; set; }
        public string State { get; set; }
        public long ElapsedMs { get; set; }
        public string Phase { get; set; }
        public double? Percent { get; set; }
        public long? Count { get; set; }
        // small, medium or large; known once the job was estimated
        public string Cost { get; set; }
        public string Error { get; set; }
        public int? ErrorStatus { get; set; }
        public object Result { get; set; }
    }

    /// <summary>A query job of any client, as the activity panel shows it.</summary>
    public class RunningQueryDto
    {
        public string Key { get; set; }
        public string Kind { get; set; }
        // small, medium or large
        public string Cost { get; set; }
        public string State { get; set; }
        public string Phase { get; set; }
        public double? Percent { get; set; }
        public long ElapsedMs { get; set; }
        // Since the job got its slot; 0 while it waits
        public long RunningMs { get; set; }
        public int Clients { get; set; }
        // A job of the client that asked
        public bool Mine { get; set; }
    }

    /// <summary>The level of the load guard.</summary>
    public class SystemLoadDto
    {
        // normal, tense or critical
        public string Level { get; set; }
        public string Reason { get; set; }
        public double Seconds { get; set; }
    }

    public class EventLogCountDto
    {
        public long Total { get; set; }
    }

    // How often one alarm entered one state, per bucket between the edges of the request
    public class AlarmCountsDto
    {
        public DateTime[] Edges { get; set; } = [];
        public long[] Counts { get; set; } = [];
        // Set while the alarms from before the counting are counted: the counts before it are not complete yet
        public DateTime? CompleteFrom { get; set; }
        // The oldest stored alarm: the statistics go back no further; null for none
        public DateTime? Oldest { get; set; }
    }

    public class SourceLogItemDto
    {
        /// <summary>UTC with 100 ns, also the cursor (before/after) of the next read.</summary>
        public string Time { get; set; }
        public string Level { get; set; }
        public string Component { get; set; }
        public string Message { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Exception { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Props { get; set; }
    }

    public class SourceLogPageDto
    {
        public List<SourceLogItemDto> Items { get; set; } = [];
        // More events than the limit: a live read missed some, a page goes on at NextBefore
        public bool Truncated { get; set; }
        // Cursor of the next older page; null at the end of the retention
        public string NextBefore { get; set; }
    }

    public class SourceLogCountDto
    {
        public int SourceId { get; set; }
        public long Warnings { get; set; }
        public long Errors { get; set; }
    }

    public class EventLogSummaryDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public long Total { get; set; }
        public long ActiveAlarms { get; set; }
        public Dictionary<string, long> CountByLevel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class ArchiveSettingsDto
    {
        public long? ValuesPerSec { get; set; }
        public double? ValueCountSumMio { get; set; }
        public double? DatabaseDiskSizeGb { get; set; }
        public DateTime? StatsUpdatedAt { get; set; }
        public string DatabaseVersion { get; set; }
        public long? RetentionSeconds { get; set; }
    }

    public class ArchiveRetentionWriteDto
    {
        public long EverySeconds { get; set; }
    }

    public class AlarmClassDto
    {
        public string Name { get; set; }
        public string Symbol { get; set; }
    }

    public class ChartTreeDto
    {
        public int MaxPlots { get; set; }
        public int MaxNodes { get; set; }
        public List<ChartTreeItemDto> Items { get; set; } = [];
    }

    public class ChartTreeItemDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        // Root, Folder or Chart
        public string Type { get; set; }
        public int ParentId { get; set; }
        public int PlotCount { get; set; }
        /// <summary>The place among the children of the parent, from 0.</summary>
        public int SortOrder { get; set; }
    }

    public class ChartPlotDto
    {
        public int Id { get; set; }
        public int DataTagId { get; set; }
        public int DataSourceId { get; set; }
        public string SourceName { get; set; }
        public string TagName { get; set; }
        public string DisplayName { get; set; }
        public bool IsBool { get; set; }
        public bool IsActive { get; set; }
        public bool IsSys { get; set; }
        public bool IsDeleted { get; set; }
    }

    public class ChartDetailDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int ParentId { get; set; }
        public List<ChartPlotDto> Plots { get; set; } = [];
    }

    public class ChartCreateDto
    {
        public string Name { get; set; }
        // Folder or Chart
        public string Type { get; set; }
        public int ParentId { get; set; }
        public List<int> DataTagIds { get; set; }
    }

    public class ChartUpdateDto
    {
        // Null keeps the name, the parent or the data tags
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public List<int> DataTagIds { get; set; }
    }

    public class ChartMoveDto
    {
        // The folder that gets the chart or folder
        public int ParentId { get; set; }
        // The child of that folder that it goes before; null puts it last
        public int? BeforeId { get; set; }
    }

    public class ChartPlotsAppendDto
    {
        public List<int> DataTagIds { get; set; } = [];
    }

    public class ChartPlotsAppendResultDto
    {
        public ChartDetailDto Chart { get; set; }
        public int Added { get; set; }
        public int Skipped { get; set; }
    }
}
