using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

public class SqliteRepository(IConfiguration config) : ISqliteRepository
{
    static SqliteRepository()
    {
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
    }

    private sealed class DataQualityTypeHandler : SqlMapper.TypeHandler<DataQuality>
    {
        public override DataQuality Parse(object value) => (DataQuality)Convert.ToInt32(value);

        public override void SetValue(IDbDataParameter parameter, DataQuality value)
        {
            parameter.DbType = DbType.Int32;
            parameter.Value = (int)value;
        }
    }
    private readonly string _connectionString = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
    private readonly int _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

    private bool _initialized;
    private readonly object _initLock = new();

    private void EnsureTable()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        connection.Execute("PRAGMA journal_mode=WAL");

        connection.Execute("""
            CREATE TABLE IF NOT EXISTS AlarmHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                AlarmCode TEXT NOT NULL,
                TriggerValue REAL NOT NULL DEFAULT 0,
                QualityCode INTEGER NOT NULL DEFAULT 0
            )
            """);

        MigrateSchema(connection);
    }

    private static void MigrateSchema(SqliteConnection connection)
    {
        TryAddColumn(connection, "TriggerValue", "REAL NOT NULL DEFAULT 0");
        TryAddColumn(connection, "QualityCode", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void TryAddColumn(SqliteConnection connection, string columnName, string columnDef)
    {
        try
        {
            connection.Execute($"ALTER TABLE AlarmHistory ADD COLUMN {columnName} {columnDef}");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    public List<AlarmRecordEntity> GetAlarmHistory()
    {
        EnsureTable();

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection.Query<AlarmRecordEntity>(
                    "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC LIMIT @Limit",
                    new { Limit = _queryLimit })
                .AsList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警历史失败");
            return [];
        }
    }

    public void SaveAlarmRecord(AlarmRecordEntity alarmRecord)
    {
        EnsureTable();

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            connection.Execute(
                """
                INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode, TriggerValue, QualityCode)
                VALUES (@DeviceId, @Timestamp, @AlarmCode, @TriggerValue, @QualityCode)
                """,
                alarmRecord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存报警记录失败: {Message}", ex.Message);
        }
    }
}
