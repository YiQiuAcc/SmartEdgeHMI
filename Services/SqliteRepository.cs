using System.Data;
using System.IO;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Infrastructure;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

public sealed class SqliteRepository : ITelemetryRepository, IAlarmRepository, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly int _queryLimit;

    // 遥测批量写入
    private readonly Channel<SensorReadingEntity> _telemetryChannel;
    private readonly CancellationTokenSource _telemetryCts;
    private readonly Task _telemetryConsumerTask;

    private const int TelemetryBatchSize = 50;
    private const int TelemetryFlushIntervalMs = 10_000;

    private bool _disposed;

    static SqliteRepository()
    {
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
    }

    public SqliteRepository(IConfiguration config)
    {
        var raw = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
        _connectionString = ResolveConnectionString(raw);
        _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

        _telemetryChannel = Channel.CreateBounded<SensorReadingEntity>(new BoundedChannelOptions(TelemetryBatchSize * 10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });
        _telemetryCts = new CancellationTokenSource();
        _telemetryConsumerTask = Task.Run(() => ConsumeTelemetryAsync(_telemetryCts.Token));
    }

    private static string ResolveConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, builder.DataSource));
        }
        return builder.ConnectionString;
    }

    private async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // ===== IAlarmRepository =====

    public async Task<List<AlarmRecordEntity>> GetAlarmHistoryAsync(AlarmHistoryFilter? filter = null)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            // Dapper：广查询，SQL 保持简单纯粹，无 WHERE 子句
            var result = (await connection.QueryAsync<AlarmRecordEntity>(
                "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

            // LINQ：内存中多条件组合过滤
            if (filter is not null)
            {
                result = result
                    .Where(r => !filter.From.HasValue || r.Timestamp >= filter.From.Value)
                    .Where(r => !filter.To.HasValue || r.Timestamp <= filter.To.Value)
                    .Where(r => filter.DeviceId is null
                        || r.DeviceId.Equals(filter.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .Where(r => filter.AlarmCode is null
                        || r.AlarmCode.Equals(filter.AlarmCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            int limit = filter?.Limit ?? _queryLimit;
            return result.Take(limit).ToList();
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

            await connection.ExecuteAsync("""
                INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode, TriggerValue, QualityCode)
                VALUES (@DeviceId, @Timestamp, @AlarmCode, @TriggerValue, @QualityCode)
                """, alarmRecord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存报警记录失败");
        }
    }

    /// <summary>演示：Dapper 宽查询 + LINQ GroupJoin/SelectMany 层级数据组装</summary>
    public async Task<List<AlarmWithTelemetryDto>> GetAlarmsWithTelemetryContextAsync(
        AlarmHistoryFilter? filter = null, TimeSpan? telemetryWindow = null)
    {
        TimeSpan window = telemetryWindow ?? TimeSpan.FromMinutes(5);

        try
        {
            await using var connection = await CreateConnectionAsync();

            // 1. Dapper — 两张表各自一条简单 SELECT，无 JOIN
            var alarms = (await connection.QueryAsync<AlarmRecordEntity>(
                "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

            var telemetry = (await connection.QueryAsync<SensorReadingEntity>(
                "SELECT * FROM TelemetryHistory ORDER BY Timestamp ASC")).AsList();

            // 2. LINQ — 内存过滤 + GroupJoin 按 DeviceId 分组 + SelectMany 时间窗口关联
            if (filter is not null)
            {
                alarms = alarms
                    .Where(r => !filter.From.HasValue || r.Timestamp >= filter.From.Value)
                    .Where(r => !filter.To.HasValue || r.Timestamp <= filter.To.Value)
                    .Where(r => filter.DeviceId is null
                        || r.DeviceId.Equals(filter.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .Where(r => filter.AlarmCode is null
                        || r.AlarmCode.Equals(filter.AlarmCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            int limit = filter?.Limit ?? _queryLimit;
            var limitedAlarms = alarms.Take(limit).ToList();

            return limitedAlarms
                .GroupJoin(
                    telemetry,
                    alarm => alarm.DeviceId,
                    reading => reading.DeviceId,
                    (alarm, deviceReadings) => new { alarm, deviceReadings })
                .Select(x => new AlarmWithTelemetryDto
                {
                    Alarm = x.alarm,
                    SurroundingTelemetry = x.deviceReadings
                        .Where(r => r.Timestamp >= x.alarm.Timestamp - window
                                 && r.Timestamp <= x.alarm.Timestamp + window)
                        .OrderBy(r => r.Timestamp)
                        .ToList()
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警上下文失败");
            return [];
        }
    }

    // ===== ITelemetryRepository =====

    public async Task SaveTelemetryAsync(SensorReadingEntity entity)
    {
        try
        {
            await _telemetryChannel.Writer.WriteAsync(entity);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "遥测记录入队失败");
        }
    }

    public async Task<List<SensorReadingEntity>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            string sql = """
            SELECT * FROM TelemetryHistory
            WHERE Timestamp >= @From AND Timestamp <= @To
            ORDER BY Timestamp ASC
            """;

            var rawData = (await connection.QueryAsync<SensorReadingEntity>(sql,
                new { From = from, To = to })).AsList();

            if (rawData.Count <= targetPoints)
                return rawData;

            return LttbDownsampler.Downsample(rawData, targetPoints);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取遥测历史失败");
            return [];
        }
    }

    private async Task ConsumeTelemetryAsync(CancellationToken ct)
    {
        var batch = new List<SensorReadingEntity>(TelemetryBatchSize);
        var lastFlush = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TelemetryFlushIntervalMs);

                try
                {
                    if (await _telemetryChannel.Reader.WaitToReadAsync(timeoutCts.Token))
                    {
                        while (batch.Count < TelemetryBatchSize && _telemetryChannel.Reader.TryRead(out var item))
                            batch.Add(item);
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) { }

                bool countReached = batch.Count >= TelemetryBatchSize;
                bool timeReached = batch.Count > 0 && (DateTime.UtcNow - lastFlush).TotalMilliseconds >= TelemetryFlushIntervalMs;

                if (countReached || timeReached)
                {
                    await FlushTelemetryBatchAsync(batch);
                    batch.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error(ex, "遥测批量消费循环异常"); }
        }

        while (_telemetryChannel.Reader.TryRead(out var remaining))
            batch.Add(remaining);

        if (batch.Count > 0)
            await FlushTelemetryBatchAsync(batch);
    }

    private async Task FlushTelemetryBatchAsync(List<SensorReadingEntity> batch)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();
            using var transaction = connection.BeginTransaction();

            const string sql = """
            INSERT INTO TelemetryHistory (DeviceId, Timestamp, Temperature, Humidity, StatusCode, ErrorCode, QualityCode)
            VALUES (@DeviceId, @Timestamp, @Temperature, @Humidity, @StatusCode, @ErrorCode, @QualityCode)
            """;

            await connection.ExecuteAsync(sql, batch, transaction: transaction);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量保存遥测记录失败, 丢失 {Count} 条", batch.Count);
        }
    }

    // ===== 数据库初始化（不属于任一接口，仅启动时调用） =====

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
                    QualityCode INTEGER NOT NULL DEFAULT 0
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

    // ===== 资源释放 =====

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _telemetryChannel.Writer.Complete();
        await _telemetryCts.CancelAsync();

        try { await _telemetryConsumerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }

        _telemetryCts.Dispose();
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
