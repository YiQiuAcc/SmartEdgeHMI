using System.Data;
using System.IO;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Infrastructure.Math;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Data.Repositories;

/// <summary>
/// SQLite 持久化仓储：同时实现 IAlarmRepository(报警直写)和 ITelemetryRepository(遥测批量缓冲写入)。 遥测写入采用双缓冲 Channel 模型
/// —— 采集线程仅入队, 后台消费者按批次/时间两维条件合并刷盘。
/// </summary>
public sealed class SqliteRepository : ITelemetryRepository, IAlarmRepository, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly int _queryLimit;

    // 遥测批量写入双缓冲通道：支持多生产者(多协议采集线程)单消费者(后台刷盘任务)
    private readonly Channel<SensorReadingRecord> _telemetryChannel;
    private readonly CancellationTokenSource _telemetryCts;
    private readonly Task _telemetryConsumerTask;

    private const int TelemetryBatchSize = 50;
    private const int TelemetryFlushIntervalMs = 10_000;

    private bool _disposed;

    static SqliteRepository()
    {
        // 注册 Dapper 自定义类型处理器, 使 Value Object(Temperature/Humidity/DataQuality) 能与 SQLite 的
        // REAL/INTEGER 列自动转换
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
        SqlMapper.AddTypeHandler(new TemperatureTypeHandler());
        SqlMapper.AddTypeHandler(new HumidityTypeHandler());
    }

    public SqliteRepository(IConfiguration config)
    {
        string raw = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
        _connectionString = ResolveConnectionString(raw);
        _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

        // Bounded Channel：容量 = 批次大小 × 10, 满时 Wait 阻塞生产者防止无限堆积
        _telemetryChannel = Channel.CreateBounded<SensorReadingRecord>(new BoundedChannelOptions(TelemetryBatchSize * 10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false, // 多协议采集线程可并发写入
            SingleReader = true   // 仅一个后台消费者
        });
        _telemetryCts = new CancellationTokenSource();
        _telemetryConsumerTask = Task.Run(() => ConsumeTelemetryAsync(_telemetryCts.Token));
    }

    /// <summary>解析连接字符串：若 DataSource 为相对路径, 拼接 BaseDirectory 转为绝对路径</summary>
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

    /// <summary>创建工作连接(每次调用独立连接, 便于并发读写互不阻塞)</summary>
    private async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // ──────────────── IAlarmRepository ────────────────
    public async Task<List<AlarmRecord>> GetAlarmHistoryAsync(AlarmHistoryFilter? filter = null)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            var result = (await connection.QueryAsync<AlarmRecord>(
                "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

            if (filter is not null)
            {
                result = [.. result
                    .Where(r => (!filter.From.HasValue || r.Timestamp >= filter.From.Value) &&
                    (!filter.To.HasValue || r.Timestamp <= filter.To.Value) &&
                    (filter.DeviceId is null || r.DeviceId.Equals(filter.DeviceId, StringComparison.OrdinalIgnoreCase)) &&
                    (filter.AlarmCode is null || r.AlarmCode.Equals(filter.AlarmCode, StringComparison.OrdinalIgnoreCase)))];
            }

            int limit = filter?.Limit ?? _queryLimit;
            return [.. result.Take(limit)];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警历史失败");
            return [];
        }
    }

    public async Task<long> SaveAlarmRecordAsync(AlarmRecord alarmRecord)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            return await connection.ExecuteScalarAsync<long>("""
                INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode, TriggerValue, QualityCode, State)
                VALUES (@DeviceId, @Timestamp, @AlarmCode, @TriggerValue, @QualityCode, @State);
                SELECT last_insert_rowid();
                """, alarmRecord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存报警记录失败");
            return 0;
        }
    }

    public async Task UpdateAlarmStatesAsync(IEnumerable<AlarmRecord> alarms)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();
            using var transaction = connection.BeginTransaction();

            await connection.ExecuteAsync("""
                UPDATE AlarmHistory
                SET State = @State, AcknowledgedAt = @AcknowledgedAt, ClearedAt = @ClearedAt
                WHERE Id = @Id
                """, alarms, transaction: transaction);

            transaction.Commit();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量更新报警状态失败");
        }
    }

    public async Task<List<AlarmWithTelemetry>> GetAlarmsWithTelemetryContextAsync(
        AlarmHistoryFilter? filter = null, TimeSpan? telemetryWindow = null)
    {
        TimeSpan window = telemetryWindow ?? TimeSpan.FromMinutes(5);

        try
        {
            await using var connection = await CreateConnectionAsync();

            var alarms = (await connection.QueryAsync<AlarmRecord>(
                "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

            var telemetry = (await connection.QueryAsync<SensorReadingRecord>(
                "SELECT * FROM TelemetryHistory ORDER BY Timestamp ASC")).AsList();

            if (filter is not null)
            {
                alarms = [.. alarms
                    .Where(r => (!filter.From.HasValue || r.Timestamp >= filter.From.Value) &&
                    (!filter.To.HasValue || r.Timestamp <= filter.To.Value) &&
                    (filter.DeviceId is null || r.DeviceId.Equals(filter.DeviceId, StringComparison.OrdinalIgnoreCase)) &&
                    (filter.AlarmCode is null || r.AlarmCode.Equals(filter.AlarmCode, StringComparison.OrdinalIgnoreCase)))];
            }

            int limit = filter?.Limit ?? _queryLimit;
            var limitedAlarms = alarms.Take(limit).ToList();

            return [.. limitedAlarms
                .GroupJoin(
                    telemetry,
                    alarm => alarm.DeviceId,
                    reading => reading.DeviceId,
                    (alarm, deviceReadings) => new { alarm, deviceReadings })
                .Select(x => new AlarmWithTelemetry
                {
                    Alarm = x.alarm,
                    SurroundingTelemetry = [.. x.deviceReadings
                        .Where(r => r.Timestamp >= x.alarm.Timestamp - window
                                 && r.Timestamp <= x.alarm.Timestamp + window)
                        .OrderBy(r => r.Timestamp)]
                })];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警上下文失败");
            return [];
        }
    }

    // ITelemetryRepository
    public async Task SaveTelemetryAsync(SensorReadingRecord entity)
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

    public async Task<List<SensorReadingRecord>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();

            // DatePicker 只选日期不带时间(00:00:00), 调整为当天最后一刻以包含全天数据
            DateTime toInclusive = to.Date.Equals(to) ? to.Date.AddDays(1).AddTicks(-1) : to;

            string sql = """
            SELECT * FROM TelemetryHistory
            WHERE Timestamp >= @From AND Timestamp <= @To
            ORDER BY Timestamp ASC
            """;

            var rawData = (await connection.QueryAsync<SensorReadingRecord>(sql,
                new { From = from, To = toInclusive })).AsList();

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
        var batch = new List<SensorReadingRecord>(TelemetryBatchSize);
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

    private async Task FlushTelemetryBatchAsync(List<SensorReadingRecord> batch)
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

        if (!columnNames.Contains("State"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN State INTEGER NOT NULL DEFAULT 0");

        if (!columnNames.Contains("AcknowledgedAt"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN AcknowledgedAt TEXT");

        if (!columnNames.Contains("ClearedAt"))
            await connection.ExecuteAsync("ALTER TABLE AlarmHistory ADD COLUMN ClearedAt TEXT");
    }

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
