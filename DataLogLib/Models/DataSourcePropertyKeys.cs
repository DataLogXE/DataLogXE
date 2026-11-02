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

namespace DataLogLib.Models
{
    /// <summary>
    /// The keys of the rows in datasourceproperty, one per property of a data source. A property without a row has the
    /// default of <see cref="DataSourceModel"/>.
    /// </summary>
    public static class DataSourcePropertyKeys
    {
        public const string MinCycle = "mincycle";
        public const string TestCycleMs = "test.cyclems";
        public const string TestTagCount = "test.tagcount";
        public const string OpcUaAlarmsEnabled = "opcua.alarmsenabled";
        public const string OpcUaLocale = "opcua.locale";
        public const string OpcUaAvailableLocales = "opcua.availablelocales";
        public const string S7AlarmsEnabled = "s7.alarmsenabled";
        public const string S7Locale = "s7.locale";
        public const string ClockWarnMs = "clock.warnms";
        public const string ClockMonitor = "clock.monitor";
        public const string ClockCompensate = "clock.compensate";
        public const string OpcUaSecurityMode = "opcua.securitymode";
        public const string OpcUaSecurityPolicy = "opcua.securitypolicy";
        public const string OpcUaCertTrust = "opcua.certtrust";
        public const string OpcUaServerCertThumbprint = "opcua.servercertthumbprint";
        public const string OpcUaServerCertSubject = "opcua.servercertsubject";
        public const string OpcUaServerCertNotAfter = "opcua.servercertnotafter";
    }
}
