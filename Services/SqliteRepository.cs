using System.Data;
using System.IO;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Infrastructure;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

public sealed class SqliteRepository : ISqliteRepository
{
    private readonly string _connectionString;
    private readonly int _queryLimit;

    // 遥测批量写入
    private readonly Channel<SensorReadingEntity> _telemetryChannel;
    private readonly CancellationTokenSource _telemetryCts;
    private readonly Task _telemetryConsumerTask;

    private const int TelemetryBatchSize = 50;
    private const int TelemetryFlushIntervalMs = 10_000;

    static SqliteRepository()
    {
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
    }

    public SqliteRepository(IConfiguration config)
    {
        var raw = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
        _connectionString = ResolveConnectionString(raw);
        _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

        // 遥测 Channel
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

    // 报警历史（直接写入）

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

    // 遥测历史

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
            // Log.Debug("批量写入 {Count} 条遥测记录", batch.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量保存遥测记录失败, 丢失 {Count} 条", batch.Count);
        }
    }

    // 数据库初始化

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

    // 资源释放

    public async ValueTask DisposeAsync()
    {
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
