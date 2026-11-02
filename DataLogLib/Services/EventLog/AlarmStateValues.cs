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

namespace DataLogLib.Services.EventLog
{
    public static class AlarmStateValues
    {
        public const string Incoming = "Incoming";
        public const string IncomingOutgoing = "Incoming/Outgoing";
        public const string Removed = "Removed";
        public const string Normal = "Normal";

        public const string ActiveUnacked = "ActiveUnacked";
        public const string ActiveAcked = "ActiveAcked";
        public const string InactiveUnacked = "InactiveUnacked";
        public const string Inactive = "Inactive";
        public const string Disabled = "Disabled";
        public const string OutOfService = "OutOfService";
        public const string Suppressed = "Suppressed";
        public const string Shelved = "Shelved";
        public const string SuppressedOrShelved = "SuppressedOrShelved";

        public const string UnconfirmedSuffix = "Unconfirmed";

        private static readonly string[] Bases =
        [
            Incoming,
            IncomingOutgoing,
            Removed,
            Normal,
            ActiveUnacked,
            ActiveAcked,
            InactiveUnacked,
            Inactive,
            Disabled,
            OutOfService,
            Suppressed,
            Shelved,
            SuppressedOrShelved
        ];

        public static IReadOnlyList<string> KnownBases => Bases;

        public static string FromActiveAcked(bool active, bool acked)
        {
            if (active && !acked)
            {
                return ActiveUnacked;
            }

            if (active && acked)
            {
                return ActiveAcked;
            }

            if (!active && !acked)
            {
                return InactiveUnacked;
            }

            return Inactive;
        }

        public static string Resolve(
            bool? enabled,
            bool? outOfService,
            bool? suppressed,
            bool? suppressedOrShelved,
            bool? active,
            bool? acked,
            bool? confirmed)
        {
            string state = null;
            if (enabled == false)
            {
                state = Disabled;
            }
            else if (outOfService == true)
            {
                state = OutOfService;
            }
            else if (suppressed == true)
            {
                state = Suppressed;
            }
            else if (suppressedOrShelved == true && suppressed == false)
            {
                state = Shelved;
            }
            else if (suppressedOrShelved == true && suppressed == null)
            {
                state = SuppressedOrShelved;
            }
            else if (active.HasValue && acked.HasValue)
            {
                state = FromActiveAcked(active.Value, acked.Value);
            }

            if (state == null)
            {
                return null;
            }

            if (confirmed == false)
            {
                return state + UnconfirmedSuffix;
            }

            return state;
        }

        // WinCC journal StateText. RaiseClear notifications (no ActiveState) return to Normal.
        // Alarm conditions leave the list as Removed and stay listed as Incoming/Outgoing.
        public static string AsWinccJournalState(string alarmState, bool? retain, bool raiseClear)
        {
            var basis = Base(alarmState);
            if (basis is ActiveUnacked or ActiveAcked)
            {
                return Incoming;
            }

            if (basis is not (InactiveUnacked or Inactive))
            {
                return alarmState;
            }

            if (raiseClear)
            {
                return Normal;
            }

            if (retain == false)
            {
                return Removed;
            }

            return IncomingOutgoing;
        }

        public static string Base(string alarmState)
        {
            if (string.IsNullOrEmpty(alarmState))
            {
                return "";
            }

            if (alarmState.EndsWith(UnconfirmedSuffix, System.StringComparison.Ordinal)
                && alarmState.Length > UnconfirmedSuffix.Length)
            {
                return alarmState.Substring(0, alarmState.Length - UnconfirmedSuffix.Length);
            }

            return alarmState;
        }

        public static bool IsKnown(string alarmState)
        {
            var basis = Base(alarmState);
            for (var i = 0; i < Bases.Length; i++)
            {
                if (basis == Bases[i])
                {
                    return alarmState == basis || alarmState.EndsWith(UnconfirmedSuffix, System.StringComparison.Ordinal);
                }
            }

            return false;
        }

        public static bool IsActiveState(string alarmState)
        {
            var basis = Base(alarmState);
            return basis is Incoming or ActiveUnacked or ActiveAcked;
        }

        /// <summary>The stored states a filter matches; the filter may name several states separated by commas.</summary>
        public static string[] TokensForFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return [];
            }

            if (filter.Contains(','))
            {
                return filter
                    .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                    .SelectMany(TokensForSingle)
                    .Distinct(System.StringComparer.Ordinal)
                    .ToArray();
            }

            return TokensForSingle(filter);
        }

        private static string[] TokensForSingle(string filter)
        {
            if (!IsKnown(filter))
            {
                return [filter];
            }

            var basis = Base(filter);
            if (filter != basis)
            {
                return [filter];
            }

            return [basis, basis + UnconfirmedSuffix];
        }
    }
}
