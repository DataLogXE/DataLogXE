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

using Dapper;
using DataAccessLib.Models;
using DataLogLib.Common;
using DataLogLib.Common.Crypt;
using DataLogLib.Models;
using DataLogLib.OpcUaNodeset;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

namespace DataLogLib.Services.DataAccess
{
    internal class PostgresConnection
    {
        private const string DataSourceSelectColumns =
            "id AS Id, name AS Name, typeid AS TypeId, isactive AS IsActive, issys AS IsSys, " +
            "host AS Host, port AS Port, rack AS Rack, slot AS Slot, username AS User, pass AS Pass, " +
            "guid AS Guid, createdat AS CreatedAt";

        #region private fields
        private DataConnectionInfo _connectionInfo;
        private PostgresPools _dataSource;
        private Dictionary<string, string> _parameter = [];
        #endregion

        #region internal methods
        internal void Init(IConfiguration configuration)
        {
            _connectionInfo = new DataConnectionInfo(configuration);

            Log.Information("Init data connection: {DataConnectionInfo}", _connectionInfo);

            _dataSource = PostgresDataSources.Get(_connectionInfo.ConnectionString);
            using var connection = _dataSource.OpenConnection();
            var scriptsFolder = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "Templates",
                "postgres");
            PostgresSchemaUpgrade.Apply(connection, scriptsFolder);
            PurgeOrphanXmlImportStages(connection);
            MigrateDataSourcePasswords(connection);
        }
        #endregion

        #region private methods

        #region init
        /// <summary>
        /// Rewrites the passwords of the old DES format with <see cref="SecretProtector"/>, once per row. The update
        /// only matches a row whose value is still the old one, so two processes starting at once do no harm.
        /// </summary>
        internal static int MigrateDataSourcePasswords(NpgsqlConnection connection)
        {
            var rows = connection.Query<(int Id, string Pass, string Guid)>(
                "SELECT id, pass, guid FROM datasource WHERE pass IS NOT NULL AND pass <> '' AND pass NOT LIKE 'v2:%'")
                .ToList();
            var migrated = 0;
            foreach (var (id, pass, guid) in rows)
            {
                var plain = LegacyDesCrypt.Decrypt(pass, guid);
                if (string.IsNullOrEmpty(plain))
                {
                    Log.Warning("The stored password of data source {DsId} could not be read; enter it again", id);
                    continue;
                }

                migrated += connection.Execute(
                    "UPDATE datasource SET pass = @newPass WHERE id = @id AND pass = @oldPass",
                    new { id, oldPass = pass, newPass = SecretProtector.Protect(plain, guid) });
            }

            if (migrated > 0)
            {
                Log.Information("Protected {Count} stored data source passwords with AES-GCM", migrated);
            }

            return migrated;
        }

        /// <summary>
        /// The plain password of a row; empty (and a warning) when it does not open, e.g. after the master key was lost.
        /// </summary>
        private static string ReadPassword(DataSourceModel dsm)
        {
            try
            {
                return SecretProtector.Unprotect(dsm.Pass, dsm.Guid);
            }
            catch (CryptographicException)
            {
                Log.Warning("The stored password of data source {DsName} Id: {DsId} does not open with the master key; " +
                    "enter it again", dsm.Name, dsm.Id);
                return string.Empty;
            }
        }

        private static void PurgeOrphanXmlImportStages(NpgsqlConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "TRUNCATE xmlimportstage, xmlimporttypemap";
            cmd.ExecuteNonQuery();
        }

        #endregion

        #region common
        private IDbConnection GetConnection()
        {
            return _dataSource.OpenConnection();
        }

        internal void Close()
        {
            // The pooled data source is process-wide and still used by the process event store during shutdown.
            _dataSource?.Clear();
        }

        private string GetUid()
        {
            var uid = Guid.NewGuid().ToString();
            uid = uid.Replace("-", "").ToUpper();
            return uid;
        }

        private void UpdateDataTagChangeMarker()
        {
            ParameterSet(Consts.DATA_TAG_CHANGE_MARKER_KEY, GetUid());
        }

        /// <summary>
        /// Sets the properties of the data sources from their rows in datasourceproperty; a property without a row or
        /// with a value that does not parse keeps the default of <see cref="DataSourceModel"/>.
        /// </summary>
        private static void DataSourcePropertiesLoad(IDbConnection conn, List<DataSourceModel> dsms)
        {
            if (dsms.Count == 0)
            {
                return;
            }

            var byId = dsms.ToDictionary(dsm => dsm.Id);
            var rows = conn.Query<(int DataSourceId, string Key, string Value)>(
                "SELECT datasourceid, key, value FROM datasourceproperty WHERE datasourceid = ANY(@ids)",
                new { ids = byId.Keys.ToArray() });
            var withClockMonitor = new HashSet<int>();
            foreach (var (dataSourceId, key, value) in rows)
            {
                var dsm = byId[dataSourceId];
                switch (key)
                {
                    case DataSourcePropertyKeys.MinCycle when TryParseInt(value, out var minCycle):
                        dsm.MinCycle = minCycle;
                        break;
                    case DataSourcePropertyKeys.TestCycleMs when TryParseInt(value, out var cycleMs):
                        dsm.TestCycleMs = cycleMs;
                        break;
                    case DataSourcePropertyKeys.TestTagCount when TryParseInt(value, out var tagCount):
                        dsm.TestTagCount = tagCount;
                        break;
                    case DataSourcePropertyKeys.OpcUaAlarmsEnabled when bool.TryParse(value, out var alarmsEnabled):
                        dsm.OpcUaAlarmsEnabled = alarmsEnabled;
                        break;
                    case DataSourcePropertyKeys.OpcUaLocale:
                        dsm.OpcUaLocale = value ?? "";
                        break;
                    case DataSourcePropertyKeys.S7AlarmsEnabled when bool.TryParse(value, out var s7AlarmsEnabled):
                        dsm.S7AlarmsEnabled = s7AlarmsEnabled;
                        break;
                    case DataSourcePropertyKeys.S7Locale:
                        dsm.S7Locale = value ?? "";
                        break;
                    case DataSourcePropertyKeys.ClockWarnMs when TryParseInt(value, out var clockWarnMs):
                        dsm.ClockWarnMs = clockWarnMs;
                        break;
                    case DataSourcePropertyKeys.ClockMonitor when bool.TryParse(value, out var clockMonitor):
                        dsm.ClockMonitor = clockMonitor;
                        withClockMonitor.Add(dataSourceId);
                        break;
                    case DataSourcePropertyKeys.ClockCompensate when bool.TryParse(value, out var clockCompensate):
                        dsm.ClockCompensate = clockCompensate;
                        break;
                    case DataSourcePropertyKeys.OpcUaSecurityMode when OpcUaSecurityValues.Modes.Contains(value):
                        dsm.OpcUaSecurityMode = value;
                        break;
                    case DataSourcePropertyKeys.OpcUaSecurityPolicy when !string.IsNullOrWhiteSpace(value):
                        dsm.OpcUaSecurityPolicy = value;
                        break;
                    case DataSourcePropertyKeys.OpcUaCertTrust when OpcUaSecurityValues.Trusts.Contains(value):
                        dsm.OpcUaCertTrust = value;
                        break;
                    case DataSourcePropertyKeys.OpcUaServerCertThumbprint:
                        dsm.OpcUaServerCertThumbprint = OpcUaSecurityValues.NormalizeThumbprint(value);
                        break;
                    case DataSourcePropertyKeys.OpcUaServerCertSubject:
                        dsm.OpcUaServerCertSubject = value ?? "";
                        break;
                    case DataSourcePropertyKeys.OpcUaServerCertNotAfter
                        when DateTime.TryParse(value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var notAfter):
                        dsm.OpcUaServerCertNotAfter = notAfter;
                        break;
                    case DataSourcePropertyKeys.OpcUaAvailableLocales:
                        dsm.OpcUaAvailableLocales = (value ?? "")
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList();
                        break;
                }
            }

            // Before clock.monitor, a threshold of 0 turned the clock watch off.
            foreach (var dsm in dsms)
            {
                if (!withClockMonitor.Contains(dsm.Id) && dsm.ClockWarnMs <= 0)
                {
                    dsm.ClockMonitor = false;
                    dsm.ClockWarnMs = DataSourceModel.DefaultClockWarnMs;
                }
            }
        }

        /// <summary>
        /// Writes the properties of the data source that belong to its type, one row each.
        /// </summary>
        private static void DataSourcePropertiesSave(IDbConnection conn, IDbTransaction ta, DataSourceModel dsm)
        {
            var properties = new List<(string Key, string Value)>
            {
                (DataSourcePropertyKeys.MinCycle, FormatInt(dsm.MinCycle))
            };
            switch ((DataSourceType)dsm.TypeId)
            {
                case DataSourceType.Test:
                    properties.Add((DataSourcePropertyKeys.TestCycleMs, FormatInt(dsm.TestCycleMs)));
                    properties.Add((DataSourcePropertyKeys.TestTagCount, FormatInt(dsm.TestTagCount)));
                    break;
                case DataSourceType.OpcUa:
                    properties.Add((DataSourcePropertyKeys.OpcUaAlarmsEnabled, dsm.OpcUaAlarmsEnabled ? "true" : "false"));
                    properties.Add((DataSourcePropertyKeys.OpcUaLocale, dsm.OpcUaLocale ?? ""));
                    properties.Add((DataSourcePropertyKeys.OpcUaAvailableLocales,
                        string.Join(",", dsm.OpcUaAvailableLocales ?? [])));
                    properties.Add((DataSourcePropertyKeys.ClockWarnMs, FormatInt(dsm.ClockWarnMs)));
                    properties.Add((DataSourcePropertyKeys.ClockMonitor, dsm.ClockMonitor ? "true" : "false"));
                    properties.Add((DataSourcePropertyKeys.ClockCompensate, dsm.ClockCompensate ? "true" : "false"));
                    properties.Add((DataSourcePropertyKeys.OpcUaSecurityMode, dsm.OpcUaSecurityMode ?? OpcUaSecurityValues.ModeNone));
                    properties.Add((DataSourcePropertyKeys.OpcUaSecurityPolicy, dsm.OpcUaSecurityPolicy ?? OpcUaSecurityValues.PolicyBest));
                    properties.Add((DataSourcePropertyKeys.OpcUaCertTrust, dsm.OpcUaCertTrust ?? OpcUaSecurityValues.TrustAny));
                    properties.Add((DataSourcePropertyKeys.OpcUaServerCertThumbprint,
                        OpcUaSecurityValues.NormalizeThumbprint(dsm.OpcUaServerCertThumbprint)));
                    properties.Add((DataSourcePropertyKeys.OpcUaServerCertSubject, dsm.OpcUaServerCertSubject ?? ""));
                    properties.Add((DataSourcePropertyKeys.OpcUaServerCertNotAfter,
                        dsm.OpcUaServerCertNotAfter?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? ""));
                    break;
                case DataSourceType.S7CommPlus:
                    properties.Add((DataSourcePropertyKeys.S7AlarmsEnabled, dsm.S7AlarmsEnabled ? "true" : "false"));
                    properties.Add((DataSourcePropertyKeys.S7Locale, dsm.S7Locale ?? ""));
                    properties.Add((DataSourcePropertyKeys.ClockWarnMs, FormatInt(dsm.ClockWarnMs)));
                    properties.Add((DataSourcePropertyKeys.ClockMonitor, dsm.ClockMonitor ? "true" : "false"));
                    properties.Add((DataSourcePropertyKeys.ClockCompensate, dsm.ClockCompensate ? "true" : "false"));
                    break;
            }

            conn.Execute(
                "INSERT INTO datasourceproperty (datasourceid, key, value) " +
                "SELECT @id, key, value FROM unnest(@keys, @values) AS p(key, value) " +
                "ON CONFLICT (datasourceid, key) DO UPDATE SET value = EXCLUDED.value",
                new
                {
                    id = dsm.Id,
                    keys = properties.Select(p => p.Key).ToArray(),
                    values = properties.Select(p => p.Value).ToArray()
                },
                ta);
        }

        private static bool TryParseInt(string value, out int result)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static string FormatInt(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        #endregion

        #endregion

        #region public methods

        #region app user
        private const string AppUserSelectColumns =
            "id AS Id, name AS Name, passwordhash AS PasswordHash, role AS Role, securitystamp AS SecurityStamp, " +
            "createdat AS CreatedAt, lastloginat AS LastLoginAt";

        // Serializes the first-run setup, so two first visitors at once do not both create an administrator.
        private const long AppUserSetupLockKey = 0x4441544155534552; // "DATAUSER"

        public int AppUserCount()
        {
            using IDbConnection conn = GetConnection();
            return conn.ExecuteScalar<int>("SELECT count(*) FROM appuser");
        }

        public List<AppUserModel> AppUserGetAll()
        {
            using IDbConnection conn = GetConnection();
            return conn.Query<AppUserModel>($"SELECT {AppUserSelectColumns} FROM appuser ORDER BY lower(name)").ToList();
        }

        public AppUserModel AppUserGetById(int id)
        {
            using IDbConnection conn = GetConnection();
            return conn.QueryFirstOrDefault<AppUserModel>(
                $"SELECT {AppUserSelectColumns} FROM appuser WHERE id = @id", new { id });
        }

        public AppUserModel AppUserGetByName(string name)
        {
            using IDbConnection conn = GetConnection();
            return conn.QueryFirstOrDefault<AppUserModel>(
                $"SELECT {AppUserSelectColumns} FROM appuser WHERE lower(name) = lower(@name)", new { name });
        }

        /// <summary>
        /// Creates the user; with <paramref name="onlyIfNone"/> only while there is no user at all (the first-run
        /// setup), false when another user came first. A taken name throws "already exists".
        /// </summary>
        public bool AppUserCreate(AppUserModel user, bool onlyIfNone = false)
        {
            using IDbConnection conn = GetConnection();
            using var ta = conn.BeginTransaction();
            if (onlyIfNone)
            {
                conn.Execute("SELECT pg_advisory_xact_lock(@key)", new { key = AppUserSetupLockKey }, ta);
                if (conn.ExecuteScalar<int>("SELECT count(*) FROM appuser", transaction: ta) > 0)
                {
                    return false;
                }
            }

            try
            {
                user.Id = conn.ExecuteScalar<int>(
                    "INSERT INTO appuser (name, passwordhash, role, securitystamp) " +
                    "VALUES (@Name, @PasswordHash, @Role, @SecurityStamp) RETURNING id",
                    new { user.Name, user.PasswordHash, user.Role, user.SecurityStamp }, ta);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new InvalidOperationException($"A user named \"{user.Name}\" already exists.");
            }

            ta.Commit();
            return true;
        }

        /// <summary>
        /// Writes role, password hash and security stamp. The last administrator cannot become a viewer.
        /// </summary>
        public void AppUserUpdate(AppUserModel user)
        {
            using IDbConnection conn = GetConnection();
            using var ta = conn.BeginTransaction();
            EnsureAnotherAdminRemains(conn, ta, user.Id, user.Role != AppUserModel.RoleAdmin);
            var rows = conn.Execute(
                "UPDATE appuser SET role = @Role, passwordhash = @PasswordHash, securitystamp = @SecurityStamp " +
                "WHERE id = @Id",
                new { user.Id, user.Role, user.PasswordHash, user.SecurityStamp }, ta);
            if (rows == 0)
            {
                throw new KeyNotFoundException("The user does not exist.");
            }

            ta.Commit();
        }

        /// <summary>
        /// Deletes the user; the last administrator cannot be deleted.
        /// </summary>
        public void AppUserDelete(int id)
        {
            using IDbConnection conn = GetConnection();
            using var ta = conn.BeginTransaction();
            EnsureAnotherAdminRemains(conn, ta, id, true);
            if (conn.Execute("DELETE FROM appuser WHERE id = @id", new { id }, ta) == 0)
            {
                throw new KeyNotFoundException("The user does not exist.");
            }

            ta.Commit();
        }

        public void AppUserTouchLogin(int id)
        {
            using IDbConnection conn = GetConnection();
            conn.Execute("UPDATE appuser SET lastloginat = now() WHERE id = @id", new { id });
        }

        /// <summary>
        /// Locks the administrators and throws when <paramref name="id"/> is the only one and loses the role.
        /// </summary>
        private static void EnsureAnotherAdminRemains(IDbConnection conn, IDbTransaction ta, int id, bool losesAdmin)
        {
            if (!losesAdmin)
            {
                return;
            }

            var admins = conn.Query<int>("SELECT id FROM appuser WHERE role = 'admin' FOR UPDATE", transaction: ta).ToList();
            if (admins.Count == 1 && admins[0] == id)
            {
                throw new InvalidOperationException("The last administrator cannot be removed or made a viewer.");
            }
        }
        #endregion

        #region data source
        public List<DataSourceModel> DataSourceGetAll()
        {
            var sql = $"SELECT {DataSourceSelectColumns} FROM datasource WHERE deleting = 0";
            using (IDbConnection conn = GetConnection())
            {
                var dsms = conn.Query<DataSourceModel>(sql).ToList();
                foreach (var dsm in dsms)
                {
                    dsm.Pass = ReadPassword(dsm);
                }
                DataSourcePropertiesLoad(conn, dsms);
                return dsms;
            }
        }

        public DataSourceModel DataSourceGetById(int id)
        {
            var sql = $"SELECT {DataSourceSelectColumns} FROM datasource WHERE id = @id AND deleting = 0";
            using (IDbConnection conn = GetConnection())
            {
                var dsm = conn.QueryFirst<DataSourceModel>(sql, new { id });
                dsm.Pass = ReadPassword(dsm);
                DataSourcePropertiesLoad(conn, [dsm]);
                return dsm;
            }
        }

        public void DataSourceCreate(DataSourceModel dsm)
        {
            // Create GUID if not exist
            if (string.IsNullOrEmpty(dsm.Guid))
            {
                dsm.Guid = GetUid();
            }

            const string sql =
                "INSERT INTO datasource (name, typeid, isactive, issys, host, port, rack, slot, username, pass, guid) " +
                "VALUES (@Name, @TypeId, @IsActive, @IsSys, @Host, @Port, @Rack, @Slot, @User, @Pass, @Guid) " +
                "RETURNING id";

            using IDbConnection conn = GetConnection();
            using var ta = conn.BeginTransaction();
            dsm.Id = conn.ExecuteScalar<int>(sql, new
            {
                dsm.Name,
                dsm.TypeId,
                dsm.IsActive,
                dsm.IsSys,
                dsm.Host,
                dsm.Port,
                dsm.Rack,
                dsm.Slot,
                dsm.User,
                Pass = SecretProtector.Protect(dsm.Pass, dsm.Guid),
                dsm.Guid
            }, ta);
            DataSourcePropertiesSave(conn, ta, dsm);
            ta.Commit();
        }

        public void DataSourceUpdate(DataSourceModel dsm)
        {
            const string sql =
                "UPDATE datasource SET name = @Name, typeid = @TypeId, isactive = @IsActive, issys = @IsSys, host = @Host, port = @Port, " +
                "rack = @Rack, slot = @Slot, username = @User, pass = @Pass, guid = @Guid " +
                "WHERE id = @Id";

            using IDbConnection conn = GetConnection();
            using var ta = conn.BeginTransaction();
            conn.Execute(sql, new
            {
                dsm.Name,
                dsm.TypeId,
                dsm.IsActive,
                dsm.IsSys,
                dsm.Host,
                dsm.Port,
                dsm.Rack,
                dsm.Slot,
                dsm.User,
                Pass = SecretProtector.Protect(dsm.Pass, dsm.Guid),
                dsm.Guid,
                dsm.Id
            }, ta);
            DataSourcePropertiesSave(conn, ta, dsm);
            ta.Commit();
        }

        /// <summary>
        /// Writes only the min cycle property of a data source (the rate its server reports), so a connect does not
        /// overwrite the other properties with an older copy of the model.
        /// </summary>
        public void DataSourceMinCycleSet(int id, int minCycle)
        {
            using IDbConnection conn = GetConnection();
            conn.Execute(
                "INSERT INTO datasourceproperty (datasourceid, key, value) " +
                "SELECT id, @key, @value FROM datasource WHERE id = @id " +
                "ON CONFLICT (datasourceid, key) DO UPDATE SET value = EXCLUDED.value",
                new { id, key = DataSourcePropertyKeys.MinCycle, value = FormatInt(minCycle) });
        }

        /// <summary>
        /// Hides a data source and removes it from the charts at once; its data tags and the data source itself are
        /// removed by <see cref="DataSourceDeleteTagChunk"/> and <see cref="DataSourceDeleteRemoved"/>. False when
        /// there is no such data source or it is a system data source.
        /// </summary>
        public bool DataSourceMarkDeleting(int id)
        {
            using IDbConnection conn = GetConnection();
            using var ta = conn.BeginTransaction();
            var marked = conn.Execute(
                "UPDATE datasource SET deleting = 1 WHERE id = @id AND issys = 0 AND deleting = 0",
                new { id },
                ta);
            if (marked == 0)
            {
                return false;
            }

            conn.Execute(
                "DELETE FROM chartplot WHERE datatagid IN (SELECT id FROM datatag WHERE datasourceid = @id)",
                new { id },
                ta);
            conn.Execute("DELETE FROM uabrowserun WHERE datasourceid = @id", new { id }, ta);
            ta.Commit();
            return true;
        }

        public List<int> DataSourcesDeleting()
        {
            using IDbConnection conn = GetConnection();
            return conn.Query<int>("SELECT id FROM datasource WHERE deleting = 1 ORDER BY id").ToList();
        }

        /// <summary>Counts the active data tags of all active data sources (the tags being logged).</summary>
        /// <remarks>
        /// isactive is 0 or 1. "> 0" is a range on ix_datatag_sourcedeletedactive, "<> 0" is not: with 643 000 tags
        /// (a browse or XML import) and 135 active ones that was a full scan of 190 MB, 9.8 s instead of 21 ms on the
        /// Raspberry Pi.
        /// </remarks>
        public int DataTagsCountActive()
        {
            using IDbConnection conn = GetConnection();
            return conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM datatag t JOIN datasource s ON s.id = t.datasourceid " +
                "WHERE t.isactive > 0 AND t.isdeleted = 0 AND s.isactive <> 0 AND s.deleting = 0");
        }

        /// <summary>Deletes up to <paramref name="limit"/> data tags of a data source and returns how many.</summary>
        public int DataSourceDeleteTagChunk(int id, int limit)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText =
                "DELETE FROM datatag WHERE id IN (SELECT id FROM datatag WHERE datasourceid = @id LIMIT @limit)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@limit", limit);
            return cmd.ExecuteNonQuery();
        }

        /// <summary>Deletes a data source marked for deletion together with what is left of its data tags.</summary>
        public void DataSourceDeleteRemoved(int id)
        {
            using (var connection = (NpgsqlConnection)GetConnection())
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandTimeout = 0;
                // ON DELETE CASCADE removes the data tags a late writer may have added, and the browse tables.
                cmd.CommandText = "DELETE FROM datasource WHERE id = @id AND deleting = 1";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }

            UpdateDataTagChangeMarker();
        }

        /// <summary>Deletes a data source and its data tags at once (chunk by chunk, without a timeout).</summary>
        public void DataSourceDelete(int id)
        {
            using (IDbConnection conn = GetConnection())
            {
                conn.Execute("UPDATE datasource SET deleting = 1 WHERE id = @id", new { id });
            }

            while (DataSourceDeleteTagChunk(id, DataAccessCtrl.DataSourceDeleteChunkRows) > 0)
            {
            }

            DataSourceDeleteRemoved(id);
        }
        #endregion

        #region data tag
        public DataTagModel DataTagGetById(int id)
        {
            try
            {
                using (IDbConnection conn = GetConnection())
                {
                    var dt = conn.Query<DataTagModel>("SELECT * FROM datatag WHERE id = @id", new { id }).FirstOrDefault();
                    return dt;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                return null;
            }
        }

        public DataTagListIndex DataTagsLoadListIndex(int dataSourceId, bool includeInactive, Action<int, int, bool> progress = null)
        {
            // "> 0" rather than "<> 0", so the active tags are read from ix_datatag_sourcedeletedactive (see DataTagsCountActive)
            var activeClause = includeInactive ? "" : " AND IsActive > 0";
            using var connection = (NpgsqlConnection)GetConnection();
            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM DataTag WHERE DataSourceId = @dataSourceId AND IsDeleted = 0" + activeClause;
            countCmd.Parameters.AddWithValue("@dataSourceId", dataSourceId);
            var count = Convert.ToInt32(countCmd.ExecuteScalar());
            var index = DataTagListIndex.Create(count);
            index.IncludesInactive = includeInactive;
            if (count == 0)
            {
                progress?.Invoke(0, 0, false);
                return index;
            }

            progress?.Invoke(0, count, false);

            // Comments in the language of the source; tags with the same description (e.g. the elements of an
            // array) share one parsed text.
            var locale = DataSourceGetById(dataSourceId)?.S7Locale;
            var comments = new Dictionary<string, string>(StringComparer.Ordinal);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT Id, Name, DisplayName, Cycle, IsActive, IsSys, Address, Description FROM DataTag " +
                "WHERE DataSourceId = @dataSourceId AND IsDeleted = 0" + activeClause;
            cmd.Parameters.AddWithValue("@dataSourceId", dataSourceId);
            using var reader = cmd.ExecuteReader();
            var i = 0;
            while (i < count && reader.Read())
            {
                index.Set(
                    i,
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? "" : CommentOf(comments, reader.GetString(7), locale));
                i++;
                if ((i & 8191) == 0)
                {
                    progress?.Invoke(i, count, false);
                }
            }

            if (i != count)
            {
                index.TrimCount(i);
            }

            progress?.Invoke(i, count, false);
            progress?.Invoke(i, count, true);
            index.SortByName();
            progress?.Invoke(i, count, false);
            return index;
        }

        private static string CommentOf(Dictionary<string, string> cache, string description, string locale)
        {
            if (!cache.TryGetValue(description, out var text))
            {
                text = DataTagDescription.Resolve(description, locale);
                cache[description] = text;
            }

            return text;
        }

        public List<DataTagListItemModel> DataTagsGetByIds(int dataSourceId, IReadOnlyList<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return [];
            }

            using (IDbConnection conn = GetConnection())
            {
                return conn.Query<DataTagListItemModel>(
                    "SELECT Id, Name, DisplayName, Address, Cycle, IsActive, IsSys FROM DataTag " +
                    "WHERE DataSourceId = @dataSourceId AND IsDeleted = 0 AND Id = ANY(@ids)",
                    new { dataSourceId, ids = ids.ToArray() }).ToList();
            }
        }

        public IReadOnlyDictionary<int, string> DataTagDescriptionsGet(int dataSourceId, IReadOnlyList<int> ids)
        {
            var result = new Dictionary<int, string>();
            if (ids == null || ids.Count == 0)
            {
                return result;
            }

            using (IDbConnection conn = GetConnection())
            {
                var rows = conn.Query<DataTagModel>(
                    "SELECT Id, Description FROM DataTag " +
                    "WHERE DataSourceId = @dataSourceId AND IsDeleted = 0 AND Description IS NOT NULL AND Id = ANY(@ids)",
                    new { dataSourceId, ids = ids.ToArray() });
                foreach (var row in rows)
                {
                    result[row.Id] = row.Description;
                }
            }

            return result;
        }

        public List<DataTagModel> DataTagDetailsGetByIds(int dataSourceId, IReadOnlyList<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return [];
            }

            using (IDbConnection conn = GetConnection())
            {
                return conn.Query<DataTagModel>(
                    "SELECT Id, Guid, DataSourceId, TypeId, Name, DisplayName, Address, Description, Cycle, IsActive, IsSys, CreatedAt " +
                    "FROM DataTag WHERE DataSourceId = @dataSourceId AND IsDeleted = 0 AND Id = ANY(@ids) ORDER BY Id",
                    new { dataSourceId, ids = ids.ToArray() }).ToList();
            }
        }

        public IReadOnlyDictionary<int, string> DataTagUidsGet(IReadOnlyCollection<int> dataTagIds)
        {
            var result = new Dictionary<int, string>();
            if (dataTagIds == null || dataTagIds.Count == 0)
            {
                return result;
            }

            using (IDbConnection conn = GetConnection())
            {
                var rows = conn.Query<DataTagModel>(
                    "SELECT Id, Guid FROM DataTag WHERE Id = ANY(@ids)",
                    new { ids = dataTagIds.ToArray() });
                foreach (var row in rows)
                {
                    result[row.Id] = row.Guid;
                }
            }

            return result;
        }

        public List<DataTagModel> DataTagsGetByDataSourceId(int dataSourceId, int? deleted = null, int? active = null)
        {
            try
            {
                var sql = "SELECT Id, DataSourceId, TypeId, Name, Address, Cycle, IsActive, IsSys, IsDeleted " +
                    "FROM DataTag WHERE DataSourceId = @dataSourceId";
                if (deleted.HasValue)
                {
                    sql += " AND IsDeleted = @deleted";
                }
                if (active.HasValue)
                {
                    sql += " AND IsActive = @active";
                }

                using (IDbConnection conn = GetConnection())
                {
                    var dts = conn.Query<DataTagModel>(sql, new { dataSourceId, deleted, active }).ToList();
                    return dts;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                return [];
            }
        }

        public List<DataTagDetailModel> DataTagDetailsGet(int? dataTagId = null, int? dataSourceId = null, int? active = null)
        {
            var sql = "" +
                "select " +
                "  DataTag.Id as Id, " +
                "  DataTag.Guid as Guid, " +
                "  DataTag.TypeId as TypeId, " +
                "  DataTag.Name as Name, " +
                "  DataTag.DisplayName as DisplayName, " +
                "  DataTag.Address as Address, " +
                "  DataTag.Description as Description, " +
                "  DataTag.Cycle as Cycle, " +
                "  DataTag.IsActive as IsActive, " +
                "  DataTag.IsSys as IsSys, " +
                "  DataSource.Id as DataSourceId, " +
                "  DataSource.Name as DataSourceName, " +
                "  DataSource.TypeId as DataSourceTypeId, " +
                "  DataSource.IsSys as DataSourceIsSys " +
                "from " +
                "   DataTag " +
                "inner join DataSource on DataSource.Id = DataTag.DataSourceId and DataSource.Deleting = 0 " +
                "where true ";

            if (dataTagId.HasValue)
            {
                sql += "and DataTag.Id = @dataTagId ";
            }
            if (dataSourceId.HasValue)
            {
                sql += "and DataSource.Id = @dataSourceId ";
            }
            if (active.HasValue)
            {
                sql += "and DataTag.IsActive = @active ";
            }

            using (IDbConnection conn = GetConnection())
            {
                var dts = conn.Query<DataTagDetailModel>(sql, new { dataTagId, dataSourceId, active }).ToList();
                return dts;
            }
        }

        public DataTagModel DataTagCreate(DataTagModel dtm)
        {
            // Create GUID if not exist
            if (string.IsNullOrEmpty(dtm.Guid))
            {
                dtm.Guid = GetUid();
            }

            try
            {
                const string sql =
                    "INSERT INTO datatag (guid, datasourceid, typeid, name, address, cycle, isactive, issys) " +
                    "VALUES (@Guid, @DataSourceId, @TypeId, @Name, @Address, @Cycle, @IsActive, @IsSys) RETURNING id";

                using (IDbConnection conn = GetConnection())
                {
                    using (IDbTransaction ta = conn.BeginTransaction())
                    {
                        var id = conn.ExecuteScalar<int>(sql, new
                        {
                            dtm.Guid,
                            dtm.DataSourceId,
                            dtm.TypeId,
                            Name = dtm.Name ?? "",
                            Address = dtm.Address ?? "",
                            dtm.Cycle,
                            dtm.IsActive,
                            dtm.IsSys
                        }, ta);
                        var dt = conn.Query<DataTagModel>("SELECT * FROM datatag WHERE id = @Id", new { Id = id }, ta).FirstOrDefault();
                        ta.Commit();
                        UpdateDataTagChangeMarker();
                        return dt;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                return null;
            }
        }

        public void DataTagsCreate(List<DataTagModel> dataTagModels)
        {
            if (dataTagModels == null || dataTagModels.Count == 0)
            {
                return;
            }

            try
            {
                using (IDbConnection conn = GetConnection())
                {
                    using (IDbTransaction ta = conn.BeginTransaction())
                    {
                        foreach (DataTagModel dtm in dataTagModels)
                        {
                            // Create GUID if not exist
                            if (string.IsNullOrEmpty(dtm.Guid))
                            {
                                dtm.Guid = GetUid();
                            }

                            const string sql =
                                "INSERT INTO datatag (guid, datasourceid, typeid, name, address, cycle, isactive, issys) " +
                                "VALUES (@Guid, @DataSourceId, @TypeId, @Name, @Address, @Cycle, @IsActive, @IsSys) RETURNING id";

                            dtm.Id = conn.ExecuteScalar<int>(sql, new
                            {
                                dtm.Guid,
                                dtm.DataSourceId,
                                dtm.TypeId,
                                Name = dtm.Name ?? "",
                                Address = dtm.Address ?? "",
                                dtm.Cycle,
                                dtm.IsActive,
                                dtm.IsSys
                            }, ta);
                            if (dtm.Id <= 0)
                            {
                                throw new InvalidOperationException("Data tag insert did not return a valid id.");
                            }
                        }
                        ta.Commit();
                        UpdateDataTagChangeMarker();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                throw;
            }
        }

        public DataTagModel DataTagGetOrCreateByAddress(DataTagModel dtm)
        {
            try
            {
                // Try to get DataTag 
                using (IDbConnection conn = GetConnection())
                {
                    var dt = conn.Query<DataTagModel>(
                        "SELECT * FROM datatag WHERE datasourceid = @DataSourceId AND address = @Address",
                        new { dtm.DataSourceId, dtm.Address }).FirstOrDefault();
                    if (dt != null)
                    {
                        return dt;
                    }
                }
                // Create new DataTag if not exist
                return DataTagCreate(dtm);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                return null;
            }
        }

        public void DataTagsUpdate(List<DataTagModel> dataTagModels)
        {
            try
            {
                using (IDbConnection conn = GetConnection())
                {
                    using (var ta = conn.BeginTransaction())
                    {
                        conn.Execute(
                            "UPDATE datatag SET datasourceid = @DataSourceId, typeid = @TypeId, name = @Name, " +
                            "address = @Address, cycle = @Cycle, isactive = @IsActive, issys = @IsSys " +
                            "WHERE id = @Id",
                            dataTagModels,
                            ta);
                        ta.Commit();
                        UpdateDataTagChangeMarker();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                // The caller (API, data source) must see the failure instead of reporting success.
                throw;
            }
        }

        public void DataTagsUpdateDeleted(List<int> ids, int deleted)
        {
            try
            {
                var sql = deleted > 0
                    ? "UPDATE datatag SET isactive = 0, isdeleted = @value WHERE id = ANY(@ids)"
                    : "UPDATE datatag SET isdeleted = @value WHERE id = ANY(@ids)";
                UpdateDataTagsByIds(sql, ids, deleted);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                // The caller (API, data source) must see the failure instead of reporting success.
                throw;
            }
        }

        public void DataTagsUpdateActive(List<int> ids, int active)
        {
            try
            {
                UpdateDataTagsByIds("UPDATE datatag SET isactive = @value WHERE id = ANY(@ids)", ids, active);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                // The caller (API, data source) must see the failure instead of reporting success.
                throw;
            }
        }

        public void DataTagsUpdateCycle(List<int> ids, int cycle)
        {
            try
            {
                UpdateDataTagsByIds("UPDATE datatag SET cycle = @value WHERE id = ANY(@ids)", ids, cycle);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                // The caller (API, data source) must see the failure instead of reporting success.
                throw;
            }
        }

        public void DataTagsUpdateActiveAndCycle(List<int> ids, int? active, int? cycle)
        {
            if (ids == null || ids.Count == 0 || (active == null && cycle == null))
            {
                return;
            }

            try
            {
                // One statement, so either both values are stored or none.
                using (IDbConnection conn = GetConnection())
                {
                    conn.Execute(
                        "UPDATE datatag SET isactive = COALESCE(@active, isactive), cycle = COALESCE(@cycle, cycle) " +
                        "WHERE id = ANY(@ids)",
                        new { active, cycle, ids = ids.ToArray() });
                }

                UpdateDataTagChangeMarker();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown");
                // The caller (API, data source) must see the failure instead of reporting success.
                throw;
            }
        }

        private void UpdateDataTagsByIds(string sql, List<int> ids, int value)
        {
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            using (IDbConnection conn = GetConnection())
            {
                conn.Execute(sql, new { value, ids = ids.ToArray() });
            }

            UpdateDataTagChangeMarker();
        }

        public DataTagBrowseSyncResult DataTagsSyncFromBrowse(
            int dataSourceId,
            int cycle,
            IReadOnlyList<DataTagBrowseImportItem> items,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            items ??= [];
            return DataTagsSyncFromBrowse(
                dataSourceId,
                cycle,
                write => FillBrowseImport(write, items, cancellationToken),
                progress,
                cancellationToken);
        }

        public DataTagBrowseSyncResult DataTagsSyncFromBrowse(
            int dataSourceId,
            int cycle,
            Action<Action<IReadOnlyList<DataTagBrowseImportItem>>> fill,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = new DataTagBrowseSyncResult();
            fill ??= static _ => { };

            using var connection = (NpgsqlConnection)GetConnection();
            using var ta = connection.BeginTransaction();
            try
            {
                ExecuteBrowseSql(connection, ta, "DROP TABLE IF EXISTS browseimport");
                ExecuteBrowseSql(
                    connection,
                    ta,
                    "CREATE TEMP TABLE browseimport (" +
                    "address TEXT NOT NULL PRIMARY KEY, " +
                    "name TEXT NOT NULL, " +
                    "displayname TEXT, " +
                    "typeid INTEGER NOT NULL, " +
                    "description TEXT) ON COMMIT DROP");

                var lastProgressUtc = DateTime.MinValue;
                fill(batch =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Prepared += InsertBrowseImportBatch(connection, ta, batch);
                    ReportBrowseSyncProgress(progress, ref lastProgressUtc, false, "prepare", result.Prepared);
                });
                if (result.Prepared > 0)
                {
                    ReportBrowseSyncProgress(progress, ref lastProgressUtc, true, "prepare", result.Prepared);
                }

                // Without statistics the planner guesses the size of the freshly filled table for the joins below.
                ExecuteBrowseSql(connection, ta, "ANALYZE browseimport");

                cancellationToken.ThrowIfCancellationRequested();
                // An empty result is far more likely a failed or filtered browse than a server without
                // any variables, so it never deletes. A soft delete keeps IsActive: the data sources only
                // load tags with IsDeleted = 0, and a restored tag comes back in its previous state.
                if (result.Prepared > 0)
                {
                    result.Deleted = ExecuteBrowseSql(
                        connection,
                        ta,
                        "UPDATE DataTag SET IsDeleted = 1 " +
                        "WHERE DataSourceId = @dataSourceId AND IsSys = 0 AND IsDeleted = 0 " +
                        "AND NOT EXISTS (SELECT 1 FROM BrowseImport b WHERE b.Address = DataTag.Address)",
                        ("@dataSourceId", dataSourceId));
                }
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "delete", Count = result.Deleted });

                cancellationToken.ThrowIfCancellationRequested();
                result.Undeleted = ExecuteBrowseSql(
                    connection,
                    ta,
                    "UPDATE DataTag SET IsDeleted = 0 " +
                    "WHERE DataSourceId = @dataSourceId AND IsSys = 0 AND IsDeleted > 0 " +
                    "AND EXISTS (SELECT 1 FROM BrowseImport b WHERE b.Address = DataTag.Address)",
                    ("@dataSourceId", dataSourceId));
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "undelete", Count = result.Undeleted });

                cancellationToken.ThrowIfCancellationRequested();
                result.Created = ExecuteBrowseSql(
                    connection,
                    ta,
                    "INSERT INTO DataTag (Guid, DataSourceId, TypeId, Name, DisplayName, Address, Description, Cycle, IsActive, IsSys) " +
                    "SELECT upper(replace(gen_random_uuid()::text, '-', '')), @dataSourceId, typeid, name, displayname, address, description, @cycle, 0, 0 " +
                    "FROM BrowseImport b " +
                    "WHERE NOT EXISTS (" +
                    "  SELECT 1 FROM DataTag t " +
                    "  WHERE t.DataSourceId = @dataSourceId AND t.Address = b.Address)",
                    ("@dataSourceId", dataSourceId),
                    ("@cycle", cycle));
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "create", Count = result.Created });

                cancellationToken.ThrowIfCancellationRequested();
                result.Updated = ExecuteBrowseSql(
                    connection,
                    ta,
                    "UPDATE DataTag SET Name = b.Name, TypeId = b.TypeId, DisplayName = b.DisplayName, Description = b.Description " +
                    "FROM BrowseImport AS b " +
                    "WHERE DataTag.DataSourceId = @dataSourceId AND DataTag.IsSys = 0 " +
                    "AND DataTag.Address = b.Address " +
                    "AND (b.Name != DataTag.Name OR b.TypeId != DataTag.TypeId OR b.DisplayName IS DISTINCT FROM DataTag.DisplayName " +
                    "OR b.Description IS DISTINCT FROM DataTag.Description)",
                    ("@dataSourceId", dataSourceId));
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "update", Count = result.Updated });

                cancellationToken.ThrowIfCancellationRequested();
                ta.Commit();
            }
            catch
            {
                try
                {
                    ta.Rollback();
                }
                catch
                {
                    // The transaction is already rolled back when dispose runs.
                }

                throw;
            }

            UpdateDataTagChangeMarker();
            return result;
        }

        public const long BrowseCheckpointStaleMillis = 24L * 60L * 60L * 1000L;

        public bool BrowseCheckpointExists(int dataSourceId)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM UaBrowseRun WHERE DataSourceId = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", dataSourceId);
            var value = cmd.ExecuteScalar();
            return value != null && value != DBNull.Value;
        }

        public void BrowseCheckpointCreate(int dataSourceId)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            using var ta = connection.BeginTransaction();
            ExecuteSql(connection, ta,
                "INSERT INTO UaBrowseRun (DataSourceId, Phase, UpdatedUtc, ResumeOnConnect) VALUES (@id, 'seed', @utc, 0) " +
                "ON CONFLICT (datasourceid) DO NOTHING",
                ("@id", dataSourceId),
                ("@utc", UtcMillis()));
            ta.Commit();
        }

        public void BrowseCheckpointSetResumeOnConnect(int dataSourceId, bool resumeOnConnect)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            ExecuteSql(connection, null,
                "UPDATE UaBrowseRun SET ResumeOnConnect = @resume, UpdatedUtc = @utc WHERE DataSourceId = @id",
                ("@id", dataSourceId),
                ("@resume", resumeOnConnect ? 1 : 0),
                ("@utc", UtcMillis()));
        }

        public bool BrowseCheckpointResumeOnConnect(int dataSourceId)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(ResumeOnConnect, 0) FROM UaBrowseRun WHERE DataSourceId = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", dataSourceId);
            var value = cmd.ExecuteScalar();
            if (value == null || value == DBNull.Value)
            {
                return false;
            }

            return Convert.ToInt32(value) != 0;
        }

        public void BrowseCheckpointTouch(int dataSourceId)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            ExecuteSql(connection, null,
                "UPDATE UaBrowseRun SET UpdatedUtc = @utc WHERE DataSourceId = @id",
                ("@id", dataSourceId),
                ("@utc", UtcMillis()));
        }

        public void BrowseCheckpointDelete(int dataSourceId)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            ExecuteSql(connection, null,
                "DELETE FROM UaBrowseRun WHERE DataSourceId = @id",
                ("@id", dataSourceId));
        }

        public int BrowseCheckpointPurgeStale()
        {
            var cutoff = UtcMillis() - BrowseCheckpointStaleMillis;
            using var connection = (NpgsqlConnection)GetConnection();
            return ExecuteSql(connection, null,
                "DELETE FROM UaBrowseRun WHERE UpdatedUtc < @cutoff",
                ("@cutoff", cutoff));
        }

        public void BrowseCheckpointSetInflight(int dataSourceId, string phase, IReadOnlyList<string> nodeKeys)
        {
            nodeKeys ??= [];
            var utc = UtcMillis();
            using var connection = (NpgsqlConnection)GetConnection();
            using var ta = connection.BeginTransaction();
            ExecuteSql(connection, ta,
                "UPDATE UaBrowseRun SET Phase = @phase, UpdatedUtc = @utc WHERE DataSourceId = @id",
                ("@id", dataSourceId),
                ("@phase", phase),
                ("@utc", utc));
            ExecuteSql(connection, ta, "DELETE FROM UaBrowseInflight WHERE DataSourceId = @id", ("@id", dataSourceId));
            InsertInflight(connection, ta, dataSourceId, nodeKeys, utc);
            ta.Commit();
        }

        public void BrowseCheckpointCommitBrowse(
            int dataSourceId,
            IReadOnlyList<string> finishedParents,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<BrowseCheckpointNode> newVariables)
        {
            finishedParents ??= [];
            newObjects ??= [];
            newVariables ??= [];
            var utc = UtcMillis();
            using var connection = (NpgsqlConnection)GetConnection();
            using var ta = connection.BeginTransaction();
            ExecuteSql(connection, ta, "DELETE FROM UaBrowseInflight WHERE DataSourceId = @id", ("@id", dataSourceId));
            DeleteQueueKeys(connection, ta, dataSourceId, finishedParents);
            var visited = new List<string>(finishedParents.Count + newObjects.Count + newVariables.Count);
            visited.AddRange(finishedParents);
            visited.AddRange(newObjects);
            foreach (var variable in newVariables)
            {
                if (!string.IsNullOrWhiteSpace(variable?.NodeKey))
                {
                    visited.Add(variable.NodeKey);
                }
            }

            InsertVisited(connection, ta, dataSourceId, visited, utc);
            AppendQueue(connection, ta, dataSourceId, newObjects, utc);
            InsertPending(connection, ta, dataSourceId, newVariables, utc);
            ExecuteSql(connection, ta,
                "UPDATE UaBrowseRun SET Phase = 'browse', UpdatedUtc = @utc WHERE DataSourceId = @id",
                ("@id", dataSourceId),
                ("@utc", utc));
            ta.Commit();
        }

        public void BrowseCheckpointCommitClassify(
            int dataSourceId,
            IReadOnlyList<string> finishedVariables,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<DataTagBrowseImportItem> scalars)
        {
            finishedVariables ??= [];
            newObjects ??= [];
            scalars ??= [];
            var utc = UtcMillis();
            using var connection = (NpgsqlConnection)GetConnection();
            using var ta = connection.BeginTransaction();
            ExecuteSql(connection, ta, "DELETE FROM UaBrowseInflight WHERE DataSourceId = @id", ("@id", dataSourceId));
            DeletePendingKeys(connection, ta, dataSourceId, finishedVariables);
            InsertVisited(connection, ta, dataSourceId, newObjects, utc);
            AppendQueue(connection, ta, dataSourceId, newObjects, utc);
            InsertFound(connection, ta, dataSourceId, scalars, utc);
            ExecuteSql(connection, ta,
                "UPDATE UaBrowseRun SET Phase = 'browse', UpdatedUtc = @utc WHERE DataSourceId = @id",
                ("@id", dataSourceId),
                ("@utc", utc));
            ta.Commit();
        }

        /// <summary>
        /// Gives tags of a source a new address (old -> new) and keeps their id, history and activation. A new
        /// address that another tag of the source has already stays with that tag.
        /// </summary>
        public int DataTagsRenameAddresses(int dataSourceId, IReadOnlyList<KeyValuePair<string, string>> renames)
        {
            if (renames == null || renames.Count == 0)
            {
                return 0;
            }

            var oldAddresses = new string[renames.Count];
            var newAddresses = new string[renames.Count];
            for (var i = 0; i < renames.Count; i++)
            {
                oldAddresses[i] = renames[i].Key;
                newAddresses[i] = renames[i].Value;
            }

            using var connection = (NpgsqlConnection)GetConnection();
            return ExecuteSql(connection, null,
                "UPDATE DataTag d SET Address = r.newaddress " +
                "FROM unnest(@old, @new) AS r(oldaddress, newaddress) " +
                "WHERE d.DataSourceId = @id AND d.Address = r.oldaddress " +
                "AND NOT EXISTS (SELECT 1 FROM DataTag x WHERE x.DataSourceId = @id AND x.Address = r.newaddress)",
                ("@id", dataSourceId),
                ("@old", oldAddresses),
                ("@new", newAddresses));
        }

        public void BrowseCheckpointSetPhase(int dataSourceId, string phase)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            ExecuteSql(connection, null,
                "UPDATE UaBrowseRun SET Phase = @phase, UpdatedUtc = @utc WHERE DataSourceId = @id",
                ("@id", dataSourceId),
                ("@phase", phase),
                ("@utc", UtcMillis()));
        }

        public BrowseCheckpointState BrowseCheckpointLoad(int dataSourceId)
        {
            var state = new BrowseCheckpointState();
            using var connection = (NpgsqlConnection)GetConnection();
            using (var phase = connection.CreateCommand())
            {
                phase.CommandText = "SELECT Phase FROM UaBrowseRun WHERE DataSourceId = @id";
                phase.Parameters.AddWithValue("@id", dataSourceId);
                var value = phase.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                {
                    return state;
                }

                state.Exists = true;
                state.Phase = Convert.ToString(value);
            }

            state.Queue = ReadKeys(connection, "SELECT NodeKey FROM UaBrowseQueue WHERE DataSourceId = @id ORDER BY Seq", dataSourceId);
            state.Inflight = ReadKeys(connection, "SELECT NodeKey FROM UaBrowseInflight WHERE DataSourceId = @id ORDER BY Seq", dataSourceId);
            state.Visited = new HashSet<string>(ReadKeys(connection, "SELECT NodeKey FROM UaBrowseVisited WHERE DataSourceId = @id", dataSourceId));
            state.Pending = ReadPending(connection, dataSourceId);
            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM UaBrowseFound WHERE DataSourceId = @id";
                count.Parameters.AddWithValue("@id", dataSourceId);
                state.FoundCount = Convert.ToInt32(count.ExecuteScalar());
            }

            return state;
        }

        public DataTagBrowseSyncResult DataTagsSyncFromBrowseCheckpoint(
            int dataSourceId,
            int cycle,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken,
            bool allowDelete = true)
        {
            var result = new DataTagBrowseSyncResult();
            using var connection = (NpgsqlConnection)GetConnection();
            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM UaBrowseFound WHERE DataSourceId = @dataSourceId";
                count.Parameters.AddWithValue("@dataSourceId", dataSourceId);
                result.Prepared = Convert.ToInt32(count.ExecuteScalar());
            }

            progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "prepare", Count = result.Prepared, Finished = true });

            using var ta = connection.BeginTransaction();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // An incomplete browse (failed nodes, structure limit) or an empty result must not delete
                // the tags it did not reach. A soft delete keeps IsActive, see DataTagsSyncFromBrowse.
                if (allowDelete && result.Prepared > 0)
                {
                    result.Deleted = ExecuteBrowseSql(
                        connection,
                        ta,
                        "UPDATE DataTag SET IsDeleted = 1 " +
                        "WHERE DataSourceId = @dataSourceId AND IsSys = 0 AND IsDeleted = 0 " +
                        "AND NOT EXISTS (SELECT 1 FROM UaBrowseFound b WHERE b.DataSourceId = @dataSourceId AND b.Address = DataTag.Address)",
                        ("@dataSourceId", dataSourceId));
                }
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "delete", Count = result.Deleted });

                cancellationToken.ThrowIfCancellationRequested();
                result.Undeleted = ExecuteBrowseSql(
                    connection,
                    ta,
                    "UPDATE DataTag SET IsDeleted = 0 " +
                    "WHERE DataSourceId = @dataSourceId AND IsSys = 0 AND IsDeleted > 0 " +
                    "AND EXISTS (SELECT 1 FROM UaBrowseFound b WHERE b.DataSourceId = @dataSourceId AND b.Address = DataTag.Address)",
                    ("@dataSourceId", dataSourceId));
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "undelete", Count = result.Undeleted });

                cancellationToken.ThrowIfCancellationRequested();
                result.Created = ExecuteBrowseSql(
                    connection,
                    ta,
                    "INSERT INTO DataTag (Guid, DataSourceId, TypeId, Name, DisplayName, Address, Cycle, IsActive, IsSys) " +
                    "SELECT upper(replace(gen_random_uuid()::text, '-', '')), @dataSourceId, typeid, name, displayname, address, @cycle, 0, 0 " +
                    "FROM UaBrowseFound b " +
                    "WHERE b.DataSourceId = @dataSourceId AND NOT EXISTS (" +
                    "  SELECT 1 FROM DataTag t " +
                    "  WHERE t.DataSourceId = @dataSourceId AND t.Address = b.Address)",
                    ("@dataSourceId", dataSourceId),
                    ("@cycle", cycle));
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "create", Count = result.Created });

                cancellationToken.ThrowIfCancellationRequested();
                result.Updated = ExecuteBrowseSql(
                    connection,
                    ta,
                    "UPDATE DataTag SET Name = b.Name, TypeId = b.TypeId, DisplayName = b.DisplayName " +
                    "FROM UaBrowseFound AS b " +
                    "WHERE DataTag.DataSourceId = @dataSourceId AND DataTag.IsSys = 0 " +
                    "AND b.DataSourceId = @dataSourceId AND DataTag.Address = b.Address " +
                    "AND (b.Name != DataTag.Name OR b.TypeId != DataTag.TypeId OR b.DisplayName IS DISTINCT FROM DataTag.DisplayName)",
                    ("@dataSourceId", dataSourceId));
                progress?.Invoke(new DataTagBrowseSyncProgress { Phase = "update", Count = result.Updated });

                cancellationToken.ThrowIfCancellationRequested();
                ta.Commit();
            }
            catch
            {
                try
                {
                    ta.Rollback();
                }
                catch
                {
                    // The transaction is already rolled back when dispose runs.
                }

                throw;
            }

            UpdateDataTagChangeMarker();
            return result;
        }

        private static void InsertInflight(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            int dataSourceId,
            IReadOnlyList<string> nodeKeys,
            long utc)
        {
            var keys = NonBlankKeys(nodeKeys);
            if (keys.Length == 0)
            {
                return;
            }

            ExecuteSql(connection, ta,
                "INSERT INTO uabrowseinflight (datasourceid, seq, nodekey, updatedutc) " +
                "SELECT @id, k.ord::integer, k.key, @utc FROM unnest(@keys) WITH ORDINALITY AS k(key, ord) " +
                "ON CONFLICT DO NOTHING",
                ("@id", dataSourceId),
                ("@keys", keys),
                ("@utc", utc));
        }

        private static string[] NonBlankKeys(IReadOnlyList<string> nodeKeys)
        {
            if (nodeKeys == null || nodeKeys.Count == 0)
            {
                return [];
            }

            var keys = new List<string>(nodeKeys.Count);
            foreach (var key in nodeKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key);
                }
            }

            return keys.ToArray();
        }

        private static void InsertVisited(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            int dataSourceId,
            IReadOnlyList<string> nodeKeys,
            long utc)
        {
            var keys = NonBlankKeys(nodeKeys);
            if (keys.Length == 0)
            {
                return;
            }

            ExecuteSql(connection, ta,
                "INSERT INTO uabrowsevisited (datasourceid, nodekey, updatedutc) " +
                "SELECT @id, k, @utc FROM unnest(@keys) AS k ON CONFLICT DO NOTHING",
                ("@id", dataSourceId),
                ("@keys", keys),
                ("@utc", utc));
        }

        private static void InsertPending(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            int dataSourceId,
            IReadOnlyList<BrowseCheckpointNode> variables,
            long utc)
        {
            var keys = new List<string>(variables.Count);
            var displayNames = new List<string>(variables.Count);
            var browseNames = new List<string>(variables.Count);
            foreach (var variable in variables)
            {
                if (string.IsNullOrWhiteSpace(variable?.NodeKey))
                {
                    continue;
                }

                keys.Add(variable.NodeKey);
                displayNames.Add(variable.DisplayName);
                browseNames.Add(variable.BrowseName);
            }

            if (keys.Count == 0)
            {
                return;
            }

            ExecuteSql(connection, ta,
                "INSERT INTO uabrowsependingvar (datasourceid, nodekey, displayname, browsename, updatedutc) " +
                "SELECT @id, v.key, v.display, v.browse, @utc FROM unnest(@keys, @displays, @browses) AS v(key, display, browse) " +
                "ON CONFLICT DO NOTHING",
                ("@id", dataSourceId),
                ("@keys", keys.ToArray()),
                ("@displays", displayNames.ToArray()),
                ("@browses", browseNames.ToArray()),
                ("@utc", utc));
        }

        private static void InsertFound(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            int dataSourceId,
            IReadOnlyList<DataTagBrowseImportItem> scalars,
            long utc)
        {
            var addresses = new List<string>(scalars.Count);
            var names = new List<string>(scalars.Count);
            var displayNames = new List<string>(scalars.Count);
            var typeIds = new List<int>(scalars.Count);
            foreach (var scalar in scalars)
            {
                if (string.IsNullOrWhiteSpace(scalar?.Address))
                {
                    continue;
                }

                addresses.Add(scalar.Address);
                names.Add(string.IsNullOrWhiteSpace(scalar.Name) ? scalar.Address : scalar.Name);
                displayNames.Add(XmlImportTypeGraph.NormalizeDisplayName(scalar.DisplayName));
                typeIds.Add(scalar.TypeId);
            }

            if (addresses.Count == 0)
            {
                return;
            }

            ExecuteSql(connection, ta,
                "INSERT INTO uabrowsefound (datasourceid, address, name, displayname, typeid, updatedutc) " +
                "SELECT @id, f.address, f.name, f.display, f.typeid, @utc " +
                "FROM unnest(@addresses, @names, @displays, @typeIds) AS f(address, name, display, typeid) " +
                "ON CONFLICT DO NOTHING",
                ("@id", dataSourceId),
                ("@addresses", addresses.ToArray()),
                ("@names", names.ToArray()),
                ("@displays", displayNames.ToArray()),
                ("@typeIds", typeIds.ToArray()),
                ("@utc", utc));
        }

        private static void AppendQueue(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            int dataSourceId,
            IReadOnlyList<string> nodeKeys,
            long utc)
        {
            var keys = NonBlankKeys(nodeKeys);
            if (keys.Length == 0)
            {
                return;
            }

            ExecuteSql(connection, ta,
                "INSERT INTO uabrowsequeue (datasourceid, seq, nodekey, updatedutc) " +
                "SELECT @id, (SELECT COALESCE(MAX(seq), 0) FROM uabrowsequeue WHERE datasourceid = @id) + k.ord::integer, k.key, @utc " +
                "FROM unnest(@keys) WITH ORDINALITY AS k(key, ord) " +
                "ON CONFLICT DO NOTHING",
                ("@id", dataSourceId),
                ("@keys", keys),
                ("@utc", utc));
        }

        private static void DeleteQueueKeys(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            int dataSourceId,
            IReadOnlyList<string> nodeKeys)
        {
            var keys = NonBlankKeys(nodeKeys);
            if (keys.Length == 0)
            {
                return;
            }

            ExecuteSql(connection, ta,
                "DELETE FROM uabrowsequeue WHERE datasourceid = @id AND nodekey = ANY(@keys)",
                ("@id", dataSourceId),
                ("@keys", keys));
        }

        private static void DeletePendingKeys(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            int dataSourceId,
            IReadOnlyList<string> nodeKeys)
        {
            var keys = NonBlankKeys(nodeKeys);
            if (keys.Length == 0)
            {
                return;
            }

            ExecuteSql(connection, ta,
                "DELETE FROM uabrowsependingvar WHERE datasourceid = @id AND nodekey = ANY(@keys)",
                ("@id", dataSourceId),
                ("@keys", keys));
        }

        private static List<string> ReadKeys(NpgsqlConnection connection, string sql, int dataSourceId)
        {
            var keys = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@id", dataSourceId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    keys.Add(reader.GetString(0));
                }
            }

            return keys;
        }

        private static List<BrowseCheckpointNode> ReadPending(NpgsqlConnection connection, int dataSourceId)
        {
            var pending = new List<BrowseCheckpointNode>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT NodeKey, DisplayName, BrowseName FROM UaBrowsePendingVar WHERE DataSourceId = @id";
            cmd.Parameters.AddWithValue("@id", dataSourceId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                pending.Add(new BrowseCheckpointNode
                {
                    NodeKey = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    BrowseName = reader.IsDBNull(2) ? null : reader.GetString(2)
                });
            }

            return pending;
        }

        private static long UtcMillis()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static int ExecuteSql(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using var cmd = connection.CreateCommand();
            if (ta != null)
            {
                cmd.Transaction = ta;
            }

            cmd.CommandText = sql;
            foreach (var parameter in parameters)
            {
                cmd.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            return cmd.ExecuteNonQuery();
        }

        public DataTagBrowseSyncResult DataTagsSyncFromXmlStage(
            int dataSourceId,
            int cycle,
            Guid jobId,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = new DataTagBrowseSyncResult();
            using (var connection = (NpgsqlConnection)GetConnection())
            {
                // The stage was just loaded, updated and trimmed; without statistics the planner guesses its size.
                using (var analyze = connection.CreateCommand())
                {
                    analyze.CommandTimeout = 0;
                    analyze.CommandText = "ANALYZE xmlimportstage";
                    analyze.ExecuteNonQuery();
                }

                // One transaction for the whole apply: a cancel or an error leaves the tag list untouched
                // instead of half deleted. Dispose rolls back when Commit was not reached.
                using var transaction = connection.BeginTransaction();
                SyncExistingTagsFromStage(connection, transaction, dataSourceId, jobId, result, progress, cancellationToken);
                InsertNewTagsFromStage(connection, transaction, dataSourceId, cycle, jobId, result, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                result.Prepared = result.Created + result.Updated + result.Undeleted;
            }

            UpdateDataTagChangeMarker();
            return result;
        }

        public void XmlImportStageDelete(Guid jobId)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            ExecuteSql(connection, null, "DELETE FROM xmlimportstage WHERE jobid = @jobId", ("@jobId", jobId));
            ExecuteSql(connection, null, "DELETE FROM xmlimporttypemap WHERE jobid = @jobId", ("@jobId", jobId));
        }

        /// <summary>Empties the stage of the XML import; only while no import runs.</summary>
        public void XmlImportStageClear()
        {
            using var connection = (NpgsqlConnection)GetConnection();
            ExecuteSql(connection, null, "TRUNCATE xmlimportstage, xmlimporttypemap");
        }

        private static void SyncExistingTagsFromStage(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int dataSourceId,
            Guid jobId,
            DataTagBrowseSyncResult result,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            int total;
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM DataTag WHERE DataSourceId = @dataSourceId";
                count.Parameters.AddWithValue("@dataSourceId", dataSourceId);
                total = Convert.ToInt32(count.ExecuteScalar());
            }

            // A file without importable variables (wrong nodeset, filtered types) must not delete every tag.
            bool allowDelete;
            using (var stageCount = connection.CreateCommand())
            {
                stageCount.Transaction = transaction;
                stageCount.CommandText = "SELECT EXISTS (SELECT 1 FROM xmlimportstage WHERE jobid = @jobId AND typeid > 0)";
                stageCount.Parameters.AddWithValue("@jobId", jobId);
                allowDelete = Convert.ToBoolean(stageCount.ExecuteScalar());
            }

            var scanned = 0;
            ReportExisting(progress, scanned, total, result, false);

            // The next chunk of the tags of the source in address order (ix_datatag_sourceaddress): its upper bound
            // and its row count.
            using var bounds = connection.CreateCommand();
            bounds.Transaction = transaction;
            bounds.CommandText =
                "SELECT MAX(address), COUNT(*) FROM (" +
                "SELECT address FROM datatag " +
                "WHERE datasourceid = @dataSourceId AND address > @last " +
                "ORDER BY address LIMIT @limit) chunk";
            bounds.Parameters.AddWithValue("@dataSourceId", dataSourceId);
            var last = bounds.Parameters.Add("@last", NpgsqlTypes.NpgsqlDbType.Text);
            bounds.Parameters.AddWithValue("@limit", XmlImportLimits.InsertChunkRows);
            last.Value = "";

            // One statement per chunk: tags in the file are restored and get the name, type and display name of the
            // file (only rows that change are written), tags that are not in the file are marked deleted. Soft delete
            // keeps IsActive, so a tag that comes back in a later import is restored as it was. A restored tag whose
            // fields changed counts as restored and as updated. The stage is bounded by the chunk as well, so both
            // joins are merge joins of two index ranges; the changed rows are materialized first, so only they are
            // looked up again by id.
            using var apply = connection.CreateCommand();
            apply.Transaction = transaction;
            apply.CommandTimeout = 0;
            apply.CommandText =
                "WITH matched AS MATERIALIZED (" +
                "SELECT d.id, d.isdeleted > 0 AS restore, " +
                "(d.name IS DISTINCT FROM s.name OR d.typeid IS DISTINCT FROM s.typeid OR " +
                "d.displayname IS DISTINCT FROM s.displayname) AS change, " +
                "s.name, s.typeid, s.displayname " +
                "FROM datatag d JOIN xmlimportstage s ON s.jobid = @jobId AND s.address = d.address AND s.typeid > 0 " +
                "AND s.address > @last AND s.address <= @upper " +
                "WHERE d.datasourceid = @dataSourceId AND d.address > @last AND d.address <= @upper AND d.issys = 0 " +
                "AND (d.isdeleted > 0 OR d.name IS DISTINCT FROM s.name OR d.typeid IS DISTINCT FROM s.typeid OR " +
                "d.displayname IS DISTINCT FROM s.displayname)), " +
                "changed AS (" +
                "UPDATE datatag d SET isdeleted = 0, name = m.name, typeid = m.typeid, displayname = m.displayname " +
                "FROM matched m WHERE d.id = m.id " +
                "RETURNING m.restore, m.change), " +
                "deleted AS (" +
                "UPDATE datatag d SET isdeleted = 1 " +
                "WHERE @allowDelete AND d.datasourceid = @dataSourceId AND d.address > @last AND d.address <= @upper " +
                "AND d.issys = 0 AND d.isdeleted = 0 " +
                "AND NOT EXISTS (SELECT 1 FROM xmlimportstage s WHERE s.jobid = @jobId AND s.address = d.address " +
                "AND s.typeid > 0 AND s.address > @last AND s.address <= @upper) " +
                "RETURNING 1) " +
                "SELECT (SELECT COUNT(*) FILTER (WHERE restore) FROM changed), " +
                "(SELECT COUNT(*) FILTER (WHERE change) FROM changed), " +
                "(SELECT COUNT(*) FROM deleted)";
            apply.Parameters.AddWithValue("@dataSourceId", dataSourceId);
            apply.Parameters.AddWithValue("@jobId", jobId);
            apply.Parameters.AddWithValue("@allowDelete", allowDelete);
            var applyLast = apply.Parameters.Add("@last", NpgsqlTypes.NpgsqlDbType.Text);
            var applyUpper = apply.Parameters.Add("@upper", NpgsqlTypes.NpgsqlDbType.Text);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string upper;
                int read;
                using (var reader = bounds.ExecuteReader())
                {
                    reader.Read();
                    upper = reader.IsDBNull(0) ? null : reader.GetString(0);
                    read = (int)reader.GetInt64(1);
                }

                if (upper == null)
                {
                    break;
                }

                applyLast.Value = last.Value;
                applyUpper.Value = upper;
                using (var reader = apply.ExecuteReader())
                {
                    reader.Read();
                    result.Undeleted += (int)reader.GetInt64(0);
                    result.Updated += (int)reader.GetInt64(1);
                    result.Deleted += (int)reader.GetInt64(2);
                }

                scanned += read;
                last.Value = upper;
                ReportExisting(progress, scanned, total, result, false);
            }

            ReportExisting(progress, Math.Max(scanned, total), Math.Max(scanned, total), result, true);
        }

        private static void ReportExisting(
            Action<DataTagBrowseSyncProgress> progress,
            int scanned,
            int total,
            DataTagBrowseSyncResult result,
            bool finished)
        {
            progress?.Invoke(new DataTagBrowseSyncProgress
            {
                Phase = "existing",
                Scanned = total > 0 ? Math.Min(scanned, total) : scanned,
                Total = total,
                Finished = finished,
                Deleted = result.Deleted,
                Restored = result.Undeleted,
                Updated = result.Updated,
                Created = result.Created
            });
        }

        private static void InsertNewTagsFromStage(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int dataSourceId,
            int cycle,
            Guid jobId,
            DataTagBrowseSyncResult result,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            int total;
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM xmlimportstage WHERE jobid = @jobId AND typeid > 0";
                count.Parameters.AddWithValue("@jobId", jobId);
                total = Convert.ToInt32(count.ExecuteScalar());
            }

            var scanned = 0;
            ReportNew(progress, scanned, total, result, false);

            // The next chunk of the stage in address order: its upper bound and its row count.
            using var bounds = connection.CreateCommand();
            bounds.Transaction = transaction;
            bounds.CommandText =
                "SELECT MAX(address), COUNT(*) FROM (" +
                "SELECT address FROM xmlimportstage " +
                "WHERE jobid = @jobId AND address > @last AND typeid > 0 " +
                "ORDER BY address LIMIT @limit) chunk";
            bounds.Parameters.AddWithValue("@jobId", jobId);
            var last = bounds.Parameters.Add("@last", NpgsqlTypes.NpgsqlDbType.Text);
            bounds.Parameters.AddWithValue("@limit", XmlImportLimits.InsertChunkRows);
            last.Value = "";

            // The uid is a random prefix per import plus a counter. Inserts then append to the right edge of the
            // unique index on the guid instead of touching a random page for every row (measured: ~40 % faster).
            var uidPrefix = Guid.NewGuid().ToString("N").Substring(0, 16);
            long uidCounter = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string upper;
                int read;
                using (var reader = bounds.ExecuteReader())
                {
                    reader.Read();
                    upper = reader.IsDBNull(0) ? null : reader.GetString(0);
                    read = (int)reader.GetInt64(1);
                }

                if (upper == null)
                {
                    break;
                }

                // Rows that exist already are skipped by the anti join, which costs far less than a conflict per row
                // on a re-import; ON CONFLICT stays for safety.
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    // A chunk of 20 000 rows on a slow disk must not hit the 30 s default.
                    insert.CommandTimeout = 0;
                    insert.CommandText =
                        "INSERT INTO datatag (guid, datasourceid, typeid, name, displayname, address, cycle, isactive, issys) " +
                        "SELECT upper(@uidPrefix || lpad(to_hex(row_number() OVER (ORDER BY n.address) + @uidCounter), 16, '0')), " +
                        "@dataSourceId, n.typeid, n.name, n.displayname, n.address, @cycle, 0, 0 " +
                        "FROM xmlimportstage n " +
                        "WHERE n.jobid = @jobId AND n.typeid > 0 AND n.address > @last AND n.address <= @upper " +
                        "AND NOT EXISTS (SELECT 1 FROM datatag d WHERE d.datasourceid = @dataSourceId AND d.address = n.address " +
                        "AND d.address > @last AND d.address <= @upper) " +
                        "ON CONFLICT (datasourceid, address) DO NOTHING";
                    insert.Parameters.AddWithValue("@uidPrefix", uidPrefix);
                    insert.Parameters.AddWithValue("@uidCounter", uidCounter);
                    insert.Parameters.AddWithValue("@dataSourceId", dataSourceId);
                    insert.Parameters.AddWithValue("@cycle", cycle);
                    insert.Parameters.AddWithValue("@jobId", jobId);
                    insert.Parameters.AddWithValue("@last", (string)last.Value);
                    insert.Parameters.AddWithValue("@upper", upper);
                    result.Created += insert.ExecuteNonQuery();
                }

                uidCounter += read;
                scanned += read;
                last.Value = upper;
                ReportNew(progress, scanned, total, result, false);
            }

            ReportNew(progress, Math.Max(scanned, total), Math.Max(scanned, total), result, true);
        }

        private static void ReportNew(
            Action<DataTagBrowseSyncProgress> progress,
            int scanned,
            int total,
            DataTagBrowseSyncResult result,
            bool finished)
        {
            progress?.Invoke(new DataTagBrowseSyncProgress
            {
                Phase = "new",
                Scanned = scanned,
                Total = total,
                Finished = finished,
                Deleted = result.Deleted,
                Restored = result.Undeleted,
                Updated = result.Updated,
                Created = result.Created
            });
        }

        private static void FillBrowseImport(
            Action<IReadOnlyList<DataTagBrowseImportItem>> write,
            IReadOnlyList<DataTagBrowseImportItem> items,
            CancellationToken cancellationToken)
        {
            const int batchSize = 500;
            var index = 0;
            while (index < items.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, items.Count - index);
                if (count == items.Count)
                {
                    write(items);
                    return;
                }

                var batch = new DataTagBrowseImportItem[count];
                for (var i = 0; i < count; i++)
                {
                    batch[i] = items[index + i];
                }

                write(batch);
                index += count;
            }
        }

        private static int InsertBrowseImportBatch(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            IReadOnlyList<DataTagBrowseImportItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return 0;
            }

            var count = items.Count;
            var addresses = new string[count];
            var names = new string[count];
            var displayNames = new string[count];
            var typeIds = new int[count];
            var descriptions = new string[count];
            for (var i = 0; i < count; i++)
            {
                var item = items[i];
                var address = item?.Address ?? "";
                addresses[i] = address;
                names[i] = string.IsNullOrWhiteSpace(item?.Name) ? address : item.Name;
                displayNames[i] = XmlImportTypeGraph.NormalizeDisplayName(item?.DisplayName);
                typeIds[i] = item?.TypeId ?? 0;
                descriptions[i] = string.IsNullOrEmpty(item?.Description) ? null : item.Description;
            }

            ExecuteSql(connection, ta,
                "INSERT INTO browseimport (address, name, displayname, typeid, description) " +
                "SELECT * FROM unnest(@addresses, @names, @displayNames, @typeIds, @descriptions) " +
                "ON CONFLICT (address) DO NOTHING",
                ("@addresses", addresses),
                ("@names", names),
                ("@displayNames", displayNames),
                ("@typeIds", typeIds),
                ("@descriptions", descriptions));
            return count;
        }

        private static int ExecuteBrowseSql(
            NpgsqlConnection connection,
            NpgsqlTransaction ta,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = ta;
            cmd.CommandText = sql;
            // A browse can change tens of thousands of tags (e.g. the comments after an update); on a Pi with a large
            // datatag table that takes longer than the 30 s default.
            cmd.CommandTimeout = 0;
            foreach (var parameter in parameters)
            {
                cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            return cmd.ExecuteNonQuery();
        }

        private static void ReportBrowseSyncProgress(
            Action<DataTagBrowseSyncProgress> progress,
            ref DateTime lastProgressUtc,
            bool force,
            string phase,
            int count)
        {
            if (progress == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force && (now - lastProgressUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            lastProgressUtc = now;
            progress(new DataTagBrowseSyncProgress
            {
                Phase = phase,
                Count = count
            });
        }
        #endregion

        #region alarm class
        public List<AlarmClassModel> AlarmClassGetAll()
        {
            using var connection = (NpgsqlConnection)GetConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Name, Symbol FROM AlarmClass ORDER BY Name LIMIT 256";
            var items = new List<AlarmClassModel>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new AlarmClassModel
                {
                    Name = reader.GetString(0),
                    Symbol = reader.IsDBNull(1) ? "" : reader.GetString(1)
                });
            }

            return items;
        }

        public void AlarmClassInsert(string name, string symbol)
        {
            using var connection = (NpgsqlConnection)GetConnection();
            ExecuteSql(
                connection,
                null,
                "INSERT INTO alarmclass (name, symbol) VALUES (@name, @symbol) ON CONFLICT (name) DO NOTHING",
                ("@name", name),
                ("@symbol", symbol ?? ""));
        }
        #endregion

        #region parameter
        public string ParameterGet(string key)
        {
            lock (_parameter)
            {
                if (_parameter.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
            return "";
        }

        public void ParameterSet(string key, string value)
        {
            lock (_parameter)
            {
                _parameter[key] = value;
            }
        }
        #endregion

        #region chart
        public List<ChartModel> ChartGetAll()
        {
            var sql = "select * from Chart";
            using (IDbConnection conn = GetConnection())
            {
                var charts = conn.Query<ChartModel>(sql).ToList();
                return charts;
            }
        }

        public ChartModel ChartGetById(int id)
        {
            using (IDbConnection conn = GetConnection())
            {
                return conn.QueryFirstOrDefault<ChartModel>("SELECT * FROM chart WHERE id = @id", new { id });
            }
        }

        public List<ChartTreeItemModel> ChartTreeGet()
        {
            using (IDbConnection conn = GetConnection())
            {
                return conn.Query<ChartTreeItemModel>(
                    "SELECT c.id AS Id, c.name AS Name, c.typeid AS TypeId, c.parentid AS ParentId, " +
                    "c.sortorder AS SortOrder, COUNT(p.id)::int AS PlotCount " +
                    "FROM chart c LEFT JOIN chartplot p ON p.chartid = c.id " +
                    "GROUP BY c.id ORDER BY c.parentid, c.sortorder, c.id").ToList();
            }
        }

        public List<ChartPlotDetailModel> ChartPlotDetailsGet(int chartId)
        {
            using (IDbConnection conn = GetConnection())
            {
                return conn.Query<ChartPlotDetailModel>(
                    "SELECT p.id AS Id, p.sort AS Sort, t.id AS DataTagId, t.name AS TagName, " +
                    "t.displayname AS DisplayName, t.typeid AS TypeId, t.isactive AS IsActive, t.issys AS IsSys, " +
                    "t.isdeleted AS IsDeleted, s.id AS DataSourceId, s.name AS DataSourceName " +
                    "FROM chartplot p " +
                    "JOIN datatag t ON t.id = p.datatagid " +
                    "JOIN datasource s ON s.id = t.datasourceid " +
                    "WHERE p.chartid = @chartId ORDER BY p.sort, p.id",
                    new { chartId }).ToList();
            }
        }

        public List<DataTagModel> DataTagsGetByIdList(IReadOnlyCollection<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return [];
            }

            using (IDbConnection conn = GetConnection())
            {
                return conn.Query<DataTagModel>(
                    "SELECT Id, DataSourceId, TypeId, Name, DisplayName, IsActive, IsSys, IsDeleted FROM DataTag " +
                    "WHERE Id = ANY(@ids)",
                    new { ids = ids.ToArray() }).ToList();
            }
        }

        public ChartModel ChartCreate(ChartModel chart, IReadOnlyList<int> dataTagIds)
        {
            using (IDbConnection conn = GetConnection())
            {
                using (IDbTransaction ta = conn.BeginTransaction())
                {
                    // A new chart or folder comes last among its siblings
                    var id = conn.ExecuteScalar<int>(
                        "INSERT INTO chart (name, typeid, parentid, sortorder) VALUES (@Name, @TypeId, @ParentId, " +
                        "(SELECT COALESCE(MAX(s.sortorder), -1) + 1 FROM chart s WHERE s.parentid = @ParentId AND s.typeid <> 0)) " +
                        "RETURNING id",
                        new { chart.Name, chart.TypeId, chart.ParentId },
                        ta);
                    if (dataTagIds is { Count: > 0 })
                    {
                        ReplaceChartPlots(conn, ta, id, dataTagIds);
                    }

                    var c = conn.Query<ChartModel>("SELECT * FROM chart WHERE id = @id", new { id }, ta).FirstOrDefault();
                    ta.Commit();
                    return c;
                }
            }
        }

        public void ChartSave(ChartModel chart, IReadOnlyList<int> dataTagIds)
        {
            using (IDbConnection conn = GetConnection())
            {
                using (IDbTransaction ta = conn.BeginTransaction())
                {
                    // Locks the chart, so two saves of the same chart do not mix their plots
                    var exists = conn.ExecuteScalar<int?>(
                        "SELECT id FROM chart WHERE id = @Id FOR UPDATE", new { chart.Id }, ta);
                    if (exists == null)
                    {
                        throw new KeyNotFoundException($"Chart {chart.Id} was not found.");
                    }

                    // A chart that moves to another folder comes last there
                    conn.Execute(
                        "UPDATE chart SET name = @Name, " +
                        "sortorder = CASE WHEN parentid <> @ParentId THEN " +
                        "(SELECT COALESCE(MAX(s.sortorder), -1) + 1 FROM chart s WHERE s.parentid = @ParentId AND s.typeid <> 0 AND s.id <> @Id) " +
                        "ELSE sortorder END, parentid = @ParentId WHERE id = @Id",
                        new { chart.Name, chart.ParentId, chart.Id },
                        ta);
                    if (dataTagIds != null)
                    {
                        ReplaceChartPlots(conn, ta, chart.Id, dataTagIds);
                    }

                    ta.Commit();
                }
            }
        }

        public void ChartChildrenOrderSet(int parentId, IReadOnlyList<int> orderedIds, int? movedId)
        {
            using (IDbConnection conn = GetConnection())
            {
                using (IDbTransaction ta = conn.BeginTransaction())
                {
                    if (movedId.HasValue)
                    {
                        conn.Execute(
                            "UPDATE chart SET parentid = @parentId WHERE id = @movedId",
                            new { parentId, movedId = movedId.Value },
                            ta);
                    }

                    if (orderedIds is { Count: > 0 })
                    {
                        conn.Execute(
                            "UPDATE chart c SET sortorder = (u.ord - 1)::int " +
                            "FROM unnest(@ids) WITH ORDINALITY AS u(id, ord) WHERE c.id = u.id",
                            new { ids = orderedIds.ToArray() },
                            ta);
                    }

                    ta.Commit();
                }
            }
        }

        public void ChartPlotsSet(int chartId, IReadOnlyList<int> dataTagIds)
        {
            using (IDbConnection conn = GetConnection())
            {
                using (IDbTransaction ta = conn.BeginTransaction())
                {
                    var exists = conn.ExecuteScalar<int?>(
                        "SELECT id FROM chart WHERE id = @chartId FOR UPDATE", new { chartId }, ta);
                    if (exists == null)
                    {
                        throw new KeyNotFoundException($"Chart {chartId} was not found.");
                    }

                    ReplaceChartPlots(conn, ta, chartId, dataTagIds ?? []);
                    ta.Commit();
                }
            }
        }

        // The plots get the order of the ids: sort 0, 1, 2, ...
        private static void ReplaceChartPlots(IDbConnection conn, IDbTransaction ta, int chartId, IReadOnlyList<int> dataTagIds)
        {
            conn.Execute("DELETE FROM chartplot WHERE chartid = @chartId", new { chartId }, ta);
            if (dataTagIds.Count == 0)
            {
                return;
            }

            conn.Execute(
                "INSERT INTO chartplot (chartid, datatagid, sort) " +
                "SELECT @chartId, u.id, (u.ord - 1)::int FROM unnest(@ids) WITH ORDINALITY AS u(id, ord)",
                new { chartId, ids = dataTagIds.ToArray() },
                ta);
        }

        public ChartModel ChartCreate(ChartModel chart)
        {
            using (IDbConnection conn = GetConnection())
            {
                using (IDbTransaction ta = conn.BeginTransaction())
                {
                    var id = conn.ExecuteScalar<int>(
                        "INSERT INTO chart (name, typeid, parentid) VALUES (@Name, @TypeId, @ParentId) RETURNING id",
                        new { chart.Name, chart.TypeId, chart.ParentId },
                        ta);
                    var c = conn.Query<ChartModel>("SELECT * FROM chart WHERE id = @id", new { id }, ta).FirstOrDefault();
                    ta.Commit();
                    return c;
                }
            }
        }

        public void ChartUpdate(ChartModel chart)
        {
            using (IDbConnection conn = GetConnection())
            {
                conn.Execute(
                    "UPDATE chart SET name = @Name, typeid = @TypeId, parentid = @ParentId WHERE id = @Id",
                    new { chart.Name, chart.TypeId, chart.ParentId, chart.Id });
            }
        }

        public void ChartDelete(int chartId)
        {
            using (IDbConnection conn = GetConnection())
            {
                conn.Execute("DELETE FROM chart WHERE id = @chartId", new { chartId });
            }
        }

        public List<ChartPlotModel> ChartPlotsGetByChartId(int chartId)
        {
            using (IDbConnection conn = GetConnection())
            {
                return conn.Query<ChartPlotModel>(
                    "SELECT * FROM chartplot WHERE chartid = @chartId ORDER BY sort",
                    new { chartId }).ToList();
            }
        }

        public ChartPlotModel ChartPlotCreate(ChartPlotModel chartPlot)
        {
            using (IDbConnection conn = GetConnection())
            {
                using (IDbTransaction ta = conn.BeginTransaction())
                {
                    var id = conn.ExecuteScalar<int>(
                        "INSERT INTO chartplot (chartid, datatagid, sort) VALUES (@ChartId, @DataTagId, @Sort) RETURNING id",
                        new { chartPlot.ChartId, chartPlot.DataTagId, chartPlot.Sort },
                        ta);
                    var c = conn.Query<ChartPlotModel>("SELECT * FROM chartplot WHERE id = @id", new { id }, ta).FirstOrDefault();
                    ta.Commit();
                    return c;
                }
            }
        }

        public void ChartPlotUpdate(ChartPlotModel chartPlot)
        {
            using (IDbConnection conn = GetConnection())
            {
                conn.Execute(
                    "UPDATE chartplot SET chartid = @ChartId, datatagid = @DataTagId, sort = @Sort WHERE id = @Id",
                    new { chartPlot.ChartId, chartPlot.DataTagId, chartPlot.Sort, chartPlot.Id });
            }
        }

        public void ChartPlotDelete(int chartPlotId)
        {
            using (IDbConnection conn = GetConnection())
            {
                conn.Execute("DELETE FROM chartplot WHERE id = @chartPlotId", new { chartPlotId });
            }
        }

        #endregion

        #endregion
    }
}
