using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Database;

/// <summary>SQLite 连接工厂: 管理连接字符串、Dapper 类型处理器注册、Schema 初始化与迁移。</summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    static SqliteConnectionFactory()
    {
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
        SqlMapper.AddTypeHandler(new TemperatureTypeHandler());
        SqlMapper.AddTypeHandler(new HumidityTypeHandler());
    }

    public SqliteConnectionFactory(IConfiguration config)
    {
        string raw = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
        var builder = new SqliteConnectionStringBuilder(raw);
        if (!Path.IsPathRooted(builder.DataSource))
            builder.DataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, builder.DataSource));
        _connectionString = builder.ConnectionString;
    }

    public async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task InitializeDatabaseAsync()
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            await connection.ExecuteAsync("""
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
            """);

            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS AlarmHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeviceId TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    AlarmCode TEXT NOT NULL,
                    TriggerValue REAL NOT NULL DEFAULT 0,
                    QualityCode INTEGER NOT NULL DEFAULT 0,
                    State INTEGER NOT NULL DEFAULT 0,
                    AcknowledgedAt TEXT,
                    ClearedAt TEXT
                )
            """);

            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS TelemetryHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeviceId TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    Temperature REAL NOT NULL,
                    Humidity REAL NOT NULL,
                    StatusCode INTEGER NOT NULL,
                    ErrorCode INTEGER NOT NULL,
                    QualityCode INTEGER NOT NULL
                )
            """);

            await connection.ExecuteAsync("""
                CREATE INDEX IF NOT EXISTS idx_telemetry_timestamp ON TelemetryHistory(Timestamp)
            """);
            await connection.ExecuteAsync("""
                CREATE INDEX IF NOT EXISTS idx_telemetry_device ON TelemetryHistory(DeviceId)
            """);

            await MigrateSchemaAsync(connection);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("系统初始化失败: 无法连接到核心数据库或执行迁移脚本, 请检查配置。", ex);
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

        if (!columnNames.Contains("State"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN State INTEGER NOT NULL DEFAULT 0");

        if (!columnNames.Contains("AcknowledgedAt"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN AcknowledgedAt TEXT");

        if (!columnNames.Contains("ClearedAt"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN ClearedAt TEXT");
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

    private sealed class TemperatureTypeHandler : SqlMapper.TypeHandler<Temperature>
    {
        public override Temperature Parse(object value) => Temperature.FromCelsius(Convert.ToDouble(value));
        public override void SetValue(IDbDataParameter parameter, Temperature value)
        {
            parameter.DbType = DbType.Double;
            parameter.Value = value.Celsius;
        }
    }

    private sealed class HumidityTypeHandler : SqlMapper.TypeHandler<Humidity>
    {
        public override Humidity Parse(object value) => Humidity.FromPercent(Convert.ToDouble(value));
        public override void SetValue(IDbDataParameter parameter, Humidity value)
        {
            parameter.DbType = DbType.Double;
            parameter.Value = value.Percent;
        }
    }
}
