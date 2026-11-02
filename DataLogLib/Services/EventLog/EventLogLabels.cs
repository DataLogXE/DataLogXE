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

using System.Text.Json;

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Label text shared with the alarm table in the UI (datalog-ui/src/format.ts). The archive
    /// sorts a column by the text the table shows, so both sides map the raw values the same way.
    /// Keep this mapping in sync with format.ts.
    /// </summary>
    public static class EventLogLabels
    {
        private const string UnconfirmedSuffix = "Unconfirmed";
        private const string ClassNameSeparator = "::";
        private const string LimitStateProperty = "LimitState";

        /// <summary>Raw state without the trailing "Unconfirmed" marker.</summary>
        public static string AlarmStateBase(string state)
        {
            if (string.IsNullOrEmpty(state))
            {
                return "";
            }

            if (state.EndsWith(UnconfirmedSuffix, StringComparison.Ordinal) && state.Length > UnconfirmedSuffix.Length)
            {
                return state[..^UnconfirmedSuffix.Length];
            }

            return state;
        }

        /// <summary>State text of the table cell, including the limit state from the props JSON.</summary>
        public static string AlarmState(string state, string limitState)
        {
            var baseState = AlarmStateBase(state);
            var label = baseState switch
            {
                AlarmStateValues.Incoming => baseState,
                AlarmStateValues.IncomingOutgoing => baseState,
                AlarmStateValues.Removed => baseState,
                AlarmStateValues.Normal => baseState,
                AlarmStateValues.ActiveUnacked => "Incoming",
                AlarmStateValues.ActiveAcked => "Incoming",
                AlarmStateValues.InactiveUnacked => "Inactive, unacknowledged",
                AlarmStateValues.Inactive => "Removed",
                AlarmStateValues.Disabled => "Disabled",
                AlarmStateValues.OutOfService => "Out of service",
                AlarmStateValues.Suppressed => "Suppressed",
                AlarmStateValues.Shelved => "Shelved",
                AlarmStateValues.SuppressedOrShelved => "Suppressed or shelved",
                _ => state ?? ""
            };

            if (label.Length > 0 && !string.IsNullOrEmpty(state) && !string.Equals(state, baseState, StringComparison.Ordinal))
            {
                label += ", unconfirmed";
            }

            var limit = limitState?.Trim() ?? "";
            if (label.Length > 0 && limit.Length > 0)
            {
                return $"{label} · {limit}";
            }

            return label;
        }

        /// <summary>The LimitState entry of the props JSON, empty when the props do not carry one.</summary>
        public static string LimitStateFromProps(string props)
        {
            if (string.IsNullOrEmpty(props))
            {
                return "";
            }

            try
            {
                using var document = JsonDocument.Parse(props);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(LimitStateProperty, out var value)
                    || value.ValueKind != JsonValueKind.String)
                {
                    return "";
                }

                return value.GetString()?.Trim() ?? "";
            }
            catch (JsonException)
            {
                return "";
            }
        }

        /// <summary>Class name behind a "namespace::name" identifier.</summary>
        public static string ClassName(string name)
        {
            var text = name?.Trim() ?? "";
            var splitAt = text.IndexOf(ClassNameSeparator, StringComparison.Ordinal);
            if (splitAt < 0)
            {
                return text;
            }

            return text[(splitAt + ClassNameSeparator.Length)..].Trim();
        }

        /// <summary>Class label of the table cell: the symbol, otherwise the formatted class name.</summary>
        public static string ClassLabel(string symbol, string name)
        {
            var trimmed = symbol?.Trim() ?? "";
            return trimmed.Length > 0 ? trimmed : ClassName(name);
        }
    }
}
