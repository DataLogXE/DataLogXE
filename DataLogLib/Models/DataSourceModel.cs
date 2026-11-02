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
    public class DataSourceModel
    {
        public const int DefaultTestCycleMs = 1000;
        public const int DefaultTestTagCount = 10;

        /// <summary>
        /// The data source ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The data source name
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The ID of the data source type 
        /// </summary>
        public int TypeId { get; set; }

        /// <summary>
        /// Data acquisition is activated
        /// 0=not active; 1=active
        /// </summary>
        public int IsActive { get; set; }

        /// <summary>
        /// !=0 indicates the datasource is a system source
        /// </summary>
        public int IsSys { get; set; }

        /// <summary>
        /// The data source host address
        /// </summary>
        public string Host { get; set; } = "";

        /// <summary>
        /// The data source port
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// The PLC rack number
        /// </summary>
        public int Rack { get; set; }

        /// <summary>
        /// The PLC slot number
        /// </summary>
        public int Slot { get; set; }

        /// <summary>
        /// The data source username
        /// </summary>
        public string User { get; set; } = "";

        /// <summary>
        /// The data source password
        /// </summary>
        public string Pass { get; set; } = "";

        /// <summary>
        /// A GUID to salt the password
        /// </summary>
        public string Guid { get; set; } = "";

        /// <summary>
        /// The allowed data tag minimum cycle (property <see cref="DataSourcePropertyKeys.MinCycle"/>)
        /// </summary>
        public int MinCycle { get; set; }

        /// <summary>
        /// Test data source: the cycle of the test values in ms (property <see cref="DataSourcePropertyKeys.TestCycleMs"/>)
        /// </summary>
        public int TestCycleMs { get; set; } = DefaultTestCycleMs;

        /// <summary>
        /// Test data source: the number of test tags per type (property <see cref="DataSourcePropertyKeys.TestTagCount"/>)
        /// </summary>
        public int TestTagCount { get; set; } = DefaultTestTagCount;

        /// <summary>
        /// OPC UA data source: whether the alarms are read (property <see cref="DataSourcePropertyKeys.OpcUaAlarmsEnabled"/>)
        /// </summary>
        public bool OpcUaAlarmsEnabled { get; set; } = true;

        /// <summary>
        /// OPC UA data source: the preferred locale of the session, empty for the server default
        /// (property <see cref="DataSourcePropertyKeys.OpcUaLocale"/>)
        /// </summary>
        public string OpcUaLocale { get; set; } = "";

        /// <summary>
        /// OPC UA data source: the message security of the session, one of <see cref="OpcUaSecurityValues"/> Mode*
        /// (property <see cref="DataSourcePropertyKeys.OpcUaSecurityMode"/>). Sources from before 0.0.200 have no row
        /// and stay unsecured; new sources default to Sign &amp; Encrypt.
        /// </summary>
        public string OpcUaSecurityMode { get; set; } = OpcUaSecurityValues.ModeNone;

        /// <summary>
        /// OPC UA data source: the security policy, <see cref="OpcUaSecurityValues.PolicyBest"/> or a policy name
        /// such as Basic256Sha256 (property <see cref="DataSourcePropertyKeys.OpcUaSecurityPolicy"/>)
        /// </summary>
        public string OpcUaSecurityPolicy { get; set; } = OpcUaSecurityValues.PolicyBest;

        /// <summary>
        /// OPC UA data source: how the server certificate is checked, any or pinned to
        /// <see cref="OpcUaServerCertThumbprint"/> (property <see cref="DataSourcePropertyKeys.OpcUaCertTrust"/>)
        /// </summary>
        public string OpcUaCertTrust { get; set; } = OpcUaSecurityValues.TrustAny;

        /// <summary>
        /// OPC UA data source: the SHA-1 thumbprint (hex) of the pinned server certificate
        /// (property <see cref="DataSourcePropertyKeys.OpcUaServerCertThumbprint"/>)
        /// </summary>
        public string OpcUaServerCertThumbprint { get; set; } = "";

        /// <summary>
        /// OPC UA data source: the subject of the pinned server certificate, shown in the form
        /// (property <see cref="DataSourcePropertyKeys.OpcUaServerCertSubject"/>)
        /// </summary>
        public string OpcUaServerCertSubject { get; set; } = "";

        /// <summary>
        /// OPC UA data source: the end of the validity of the pinned server certificate (UTC), null when unknown
        /// (property <see cref="DataSourcePropertyKeys.OpcUaServerCertNotAfter"/>)
        /// </summary>
        public DateTime? OpcUaServerCertNotAfter { get; set; }

        /// <summary>
        /// S7CommPlus data source: whether the PLC alarms are received (property <see cref="DataSourcePropertyKeys.S7AlarmsEnabled"/>).
        /// Off by default: it takes a PLC subscription, and a PLC may already deliver its alarms over OPC UA.
        /// </summary>
        public bool S7AlarmsEnabled { get; set; }

        public const int DefaultClockWarnMs = 10000;

        /// <summary>
        /// OPC UA and S7CommPlus data source: a device clock off by more than this is shown on the data sources page
        /// and raises a system alarm while <see cref="ClockMonitor"/> is set (property <see cref="DataSourcePropertyKeys.ClockWarnMs"/>)
        /// </summary>
        public int ClockWarnMs { get; set; } = DefaultClockWarnMs;

        /// <summary>
        /// OPC UA and S7CommPlus data source: whether the device clock is watched against <see cref="ClockWarnMs"/>
        /// (property <see cref="DataSourcePropertyKeys.ClockMonitor"/>)
        /// </summary>
        public bool ClockMonitor { get; set; } = true;

        /// <summary>
        /// OPC UA and S7CommPlus data source: whether the time stamps of values and alarms are moved by the measured
        /// clock difference onto the local clock; off takes the device times as they are
        /// (property <see cref="DataSourcePropertyKeys.ClockCompensate"/>)
        /// </summary>
        public bool ClockCompensate { get; set; } = true;

        /// <summary>
        /// S7CommPlus data source: the language of the alarm texts (e.g. "de-DE"), empty for English
        /// (property <see cref="DataSourcePropertyKeys.S7Locale"/>)
        /// </summary>
        public string S7Locale { get; set; } = "";

        /// <summary>
        /// OPC UA data source: the locales the server reported in the last connection test
        /// (property <see cref="DataSourcePropertyKeys.OpcUaAvailableLocales"/>, comma separated)
        /// </summary>
        public List<string> OpcUaAvailableLocales { get; set; } = [];

        /// <summary>
        /// Creation timestamp
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }
}
