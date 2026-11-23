using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;

namespace SmartEdgeHMI.Data;

/// <summary>SQLite 连接工厂: 连接字符串管理、Dapper 类型处理器注册、Schema 初始化与迁移</summary>
/// <remarks>
/// 连接级 PRAGMA(<c>synchronous</c> / <c>temp_store</c>)不随库文件持久化, 因此每次打开连接后都要重新声明;
/// TypeHandler 在静态构造中注册一次, 之后所有连接共用。
/// </remarks>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// 建库与调优 PRAGMA: <c>journal_mode=WAL</c> 随库文件持久化, 其余为连接级设置需每次声明
    /// </summary>
    private const string ConnectionPragmas =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        """;

    /// <summary>Dapper 类型处理器只需注册一次, 放在静态构造里可避免每个实例重复注册</summary>
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
        // 相对路径按程序目录解析, 从不同工作目录启动时应指向同一个库文件
        if (!Path.IsPathRooted(builder.DataSource))
            builder.DataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, builder.DataSource));
        _connectionString = builder.ConnectionString;
    }

    /// <summary>新建并打开一个连接, 返回前已应用连接级 PRAGMA</summary>
    /// <remarks>调用方负责释放; 连接为每次操作新建, 不共享(由 Microsoft.Data.Sqlite 连接池复用底层句柄)</remarks>
    public async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyConnectionPragmasAsync(connection);
        return connection;
    }

    /// <summary>建库、建索引并补齐历史库缺失的列, 可重复执行</summary>
    /// <remarks>
    /// 全部使用 IF NOT EXISTS / 列存在性判断, 因此每次启动都可安全调用;
    /// 失败统一包装为 <see cref="InvalidOperationException"/>, 让调用方在启动阶段就暴露配置问题。
    /// </remarks>
    public async Task InitializeDatabaseAsync()
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            await ApplyConnectionPragmasAsync(connection);

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

            // 遥测查询以设备与时间范围为主, 由复合索引承担, 单列时间索引服务于跨设备的整段时间查询
            await connection.ExecuteAsync("""
                CREATE INDEX IF NOT EXISTS idx_telemetry_timestamp ON TelemetryHistory(Timestamp)
            """);
            await connection.ExecuteAsync("""
                CREATE INDEX IF NOT EXISTS idx_telemetry_device ON TelemetryHistory(DeviceId)
            """);
            await connection.ExecuteAsync("""
                CREATE INDEX IF NOT EXISTS idx_alarm_timestamp ON AlarmHistory(Timestamp)
            """);
            await connection.ExecuteAsync("""
                CREATE INDEX IF NOT EXISTS idx_telemetry_device_timestamp ON TelemetryHistory(DeviceId, Timestamp)
            """);

            await MigrateSchemaAsync(connection);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("系统初始化失败: 无法连接到核心数据库或执行迁移脚本, 请检查配置。", ex);
        }
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection)
        => await connection.ExecuteAsync(ConnectionPragmas);

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
