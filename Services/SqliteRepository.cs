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
    private readonly string _connectionString = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
    private readonly int _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

    static SqliteRepository()
    {
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
    }

    private async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<List<AlarmRecordEntity>> GetAlarmHistoryAsync()
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            var result = await connection.QueryAsync<AlarmRecordEntity>(
                "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC LIMIT @Limit",
                new { Limit = _queryLimit });

            return result.AsList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警历史失败");
            return [];
        }
    }

    public async Task SaveAlarmRecordAsync(AlarmRecordEntity alarmRecord)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            await connection.ExecuteAsync(
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

    public async Task InitializeDatabaseAsync()
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            await connection.ExecuteAsync("PRAGMA journal_mode=WAL");

            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS AlarmHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeviceId TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    AlarmCode TEXT NOT NULL,
                    TriggerValue REAL NOT NULL DEFAULT 0,
                    QualityCode INTEGER NOT NULL DEFAULT 0
                )
                """);

            await MigrateSchemaAsync(connection);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "数据库初始化失败");
            throw;
        }
    }

    private static async Task MigrateSchemaAsync(SqliteConnection connection)
    {
        var existingColumns = await connection.QueryAsync<string>(
            "SELECT name FROM PRAGMA_table_info('AlarmHistory')");

        var columnNames = existingColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columnNames.Contains("TriggerValue"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN TriggerValue REAL NOT NULL DEFAULT 0");

        if (!columnNames.Contains("QualityCode"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN QualityCode INTEGER NOT NULL DEFAULT 0");
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
}
