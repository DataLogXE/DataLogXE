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
using System.Text.RegularExpressions;
using DataLogService.DataSource.OpcUa;
using Opc.Ua;
using S7CommPlusDriver.Alarming;

namespace DataLogService.DataSource.S7CommPlus
{
    /// <summary>
    /// Turns an S7 alarm (DAI) into the notification the alarm tracker works with, so S7 alarms take the
    /// same path as OPC UA alarms (states, correlation, archive seed, live list).
    /// </summary>
    internal static class S7AlarmMapper
    {
        public const int DefaultLanguage = DataLogLib.Models.S7AlarmLanguages.English;

        private const int MaxPropText = 200;
        // An alarm cannot happen after it was received. A later time means the clock offset of the PLC is not
        // known yet (right after a connect): the row then takes the receive time. The event log gives the rows of a
        // source rising times, so one row in the future would hold back every following row of the source.
        private static readonly TimeSpan MaxAhead = TimeSpan.FromSeconds(5);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);

        /// <summary>
        /// Info reports and event acknowledgements are single events: they have no going and no
        /// acknowledgement. They become one history row each and never stand in the alarm list.
        /// </summary>
        public static bool IsOneShot(AlarmDai dai)
        {
            return dai.MessageType == AlarmMessageType.InfoReport || dai.MessageType == AlarmMessageType.EventAck;
        }

        /// <param name="clockShift">server clock minus PLC clock: the PLC times are moved by it, as the values of the
        /// data tags are, so a PLC clock that is off does not put alarms into the future (props keep the PLC time)</param>
        /// <param name="hardPlcTime">the source takes the PLC times as they are (no clock compensation): no shift and
        /// no limit, a PLC clock ahead puts the alarms into the future</param>
        public static OpcUaAlarmNotification Map(
            AlarmDai dai, int languageId, DateTime receivedUtc, TimeSpan clockShift = default, bool hardPlcTime = false)
        {
            var current = dai.Current;
            var plcTime = dai.LatestTimeUtc;
            var eventTime = hardPlcTime
                ? plcTime ?? receivedUtc
                : NotAfter(plcTime.HasValue ? plcTime.Value + clockShift : receivedUtc, receivedUtc);
            int lcid = dai.Texts.ChooseLanguage(languageId);
            string alarmText = Clean(AlarmTextFormatter.Format(dai.Texts.Get(lcid, AlarmTexts.AlarmText), current));
            string infoText = Clean(AlarmTextFormatter.Format(dai.Texts.Get(lcid, AlarmTexts.InfoText), current));
            string location = Clean(dai.Texts.Get(lcid, AlarmTexts.AdditionalText1));
            string id = "0x" + dai.CpuAlarmId.ToString("X16", CultureInfo.InvariantCulture);
            bool oneShot = IsOneShot(dai);

            var n = new OpcUaAlarmNotification
            {
                EventId = BuildEventId(dai),
                EventType = new NodeId("s7:" + dai.MessageType.ToString(), 0),
                EventTypeName = TypeName(dai.MessageType),
                AlarmClassName = ClassName(dai.Domain),
                // No symbol: the PLC sends no class names, the alarm pages then show the name.
                AlarmClassSymbol = "",
                Time = eventTime,
                ReceiveTime = receivedUtc,
                ObservedUtc = receivedUtc,
                Message = alarmText.Length > 0 ? alarmText : infoText.Length > 0 ? infoText : "S7 alarm " + id,
                Severity = SeverityOf(dai.HmiInfo?.Priority ?? 0),
                ConditionName = id,
                SourceName = location,
                Enabled = true,
                ExtraProps = BuildProps(dai, id, plcTime, receivedUtc, infoText, current)
            };

            if (oneShot)
            {
                // Raise/clear form: the tracker writes one "Normal" row and keeps nothing. Every
                // occurrence gets its own condition, else they would share one correlation.
                n.ConditionId = new NodeId("s7:" + id + ":" + dai.SequenceCounter.ToString(CultureInfo.InvariantCulture), 0);
                n.Retain = false;
                n.ActiveSince = eventTime;
                return n;
            }

            bool active = dai.IsActive;
            // Alarms without acknowledgement ("Notify") carry the acknowledged bit from the start.
            bool acked = dai.IsAcknowledged;
            n.ConditionId = new NodeId("s7:" + id, 0);
            n.Active = active;
            n.Acked = acked;
            n.Retain = active || !acked;
            if (dai.Coming?.TimestampUtc != null)
            {
                n.ActiveSince = hardPlcTime
                    ? dai.Coming.TimestampUtc.Value
                    : NotAfter(dai.Coming.TimestampUtc.Value + clockShift, receivedUtc);
            }
            return n;
        }

        private static DateTime NotAfter(DateTime timeUtc, DateTime receivedUtc)
        {
            return timeUtc > receivedUtc + MaxAhead ? receivedUtc : timeUtc;
        }

        // Priority 0..16 onto the OPC UA severity 1..1000 (priority 0 is 1, as the PLC's OPC UA server sends it).
        public static ushort SeverityOf(byte priority)
        {
            int p = Math.Min((int)priority, 16);
            return (ushort)(1 + (int)Math.Round(p * 999 / 16.0));
        }

        public static string ClassName(ushort domain)
        {
            if (domain == AlarmDai.DomainSystemDiagnostics) return "System diagnostics";
            if (domain == 2 || domain == 3) return "Security";
            if (domain >= AlarmDai.DomainUserClassFirst) return "Program alarms " + (domain - AlarmDai.DomainUserClassFirst).ToString(CultureInfo.InvariantCulture);
            return "S7 domain " + domain.ToString(CultureInfo.InvariantCulture);
        }

        public static string TypeName(AlarmMessageType type)
        {
            return type switch
            {
                AlarmMessageType.Alarm => "S7 alarm",
                AlarmMessageType.Notify => "S7 notification",
                AlarmMessageType.InfoReport => "S7 info report",
                AlarmMessageType.EventAck => "S7 event acknowledgement",
                _ => "S7 message",
            };
        }

        // Same alarm, same counter and same state is the same event (coming and its going share the counter).
        private static byte[] BuildEventId(AlarmDai dai)
        {
            var b = new byte[14];
            BitConverter.TryWriteBytes(b.AsSpan(0, 8), dai.CpuAlarmId);
            BitConverter.TryWriteBytes(b.AsSpan(8, 4), dai.SequenceCounter);
            b[12] = dai.AllStatesInfo;
            b[13] = (byte)(dai.IsAcknowledged ? 1 : 0);
            return b;
        }

        private static Dictionary<string, string> BuildProps(
            AlarmDai dai, string id, DateTime? plcTime, DateTime receivedUtc, string infoText, AlarmTransition current)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CpuAlarmId"] = id,
                ["Domain"] = dai.Domain.ToString(CultureInfo.InvariantCulture),
                ["MessageType"] = dai.MessageType.ToString(),
                ["AllStatesInfo"] = "0x" + dai.AllStatesInfo.ToString("X2", CultureInfo.InvariantCulture),
                ["SequenceCounter"] = dai.SequenceCounter.ToString(CultureInfo.InvariantCulture),
                ["ReceiveTime"] = receivedUtc.ToString("O", CultureInfo.InvariantCulture)
            };
            if (plcTime.HasValue)
            {
                props["PlcTime"] = plcTime.Value.ToString("O", CultureInfo.InvariantCulture);
            }
            if (dai.HmiInfo != null)
            {
                props["Priority"] = dai.HmiInfo.Priority.ToString(CultureInfo.InvariantCulture);
                props["AlarmClassId"] = dai.HmiInfo.AlarmClass.ToString(CultureInfo.InvariantCulture);
                props["ClientAlarmId"] = dai.HmiInfo.ClientAlarmId.ToString(CultureInfo.InvariantCulture);
            }
            if (infoText.Length > 0)
            {
                props["InfoText"] = Limit(infoText);
            }
            var values = AssociatedValuesText(current);
            if (values.Length > 0)
            {
                props["AssociatedValues"] = Limit(values);
            }
            if (!string.IsNullOrEmpty(dai.DecodeError))
            {
                props["DecodeError"] = Limit(dai.DecodeError);
            }
            return props;
        }

        private static string AssociatedValuesText(AlarmTransition t)
        {
            if (t == null)
            {
                return "";
            }
            var sb = new StringBuilder();
            foreach (var v in t.Values)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append("SD_").Append(v.Key.ToString(CultureInfo.InvariantCulture)).Append('=').Append(v.Value.ToString());
            }
            if (t.DiagnosticData != null)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(Convert.ToHexString(t.DiagnosticData));
            }
            return sb.ToString();
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : Spaces.Replace(text, " ").Trim();
        }

        private static string Limit(string text)
        {
            return text.Length <= MaxPropText ? text : text.Substring(0, MaxPropText);
        }
    }
}
