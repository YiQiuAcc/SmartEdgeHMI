using System.Data;
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

    // 报警批量写入
    private readonly Channel<AlarmRecordEntity> _alarmChannel;
    private readonly CancellationTokenSource _alarmCts;
    private readonly Task _alarmConsumerTask;

    // 遥测批量写入
    private readonly Channel<SensorReadingEntity> _telemetryChannel;
    private readonly CancellationTokenSource _telemetryCts;
    private readonly Task _telemetryConsumerTask;

    private const int AlarmBatchSize = 50;
    private const int AlarmFlushIntervalMs = 1000;
    private const int TelemetryBatchSize = 100;
    private const int TelemetryFlushIntervalMs = 2000;

    static SqliteRepository()
    {
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
    }

    public SqliteRepository(IConfiguration config)
    {
        _connectionString = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
        _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

        // 报警 Channel
        _alarmChannel = Channel.CreateBounded<AlarmRecordEntity>(new BoundedChannelOptions(AlarmBatchSize * 10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });
        _alarmCts = new CancellationTokenSource();
        _alarmConsumerTask = Task.Run(() => ConsumeAlarmsAsync(_alarmCts.Token));

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

    private async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // ======================== 报警历史 ========================

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
            await _alarmChannel.Writer.WriteAsync(alarmRecord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "报警记录入队失败");
        }
    }

    private async Task ConsumeAlarmsAsync(CancellationToken ct)
    {
        var batch = new List<AlarmRecordEntity>(AlarmBatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(AlarmFlushIntervalMs);

                try
                {
                    if (await _alarmChannel.Reader.WaitToReadAsync(timeoutCts.Token))
                    {
                        while (batch.Count < AlarmBatchSize && _alarmChannel.Reader.TryRead(out var item))
                            batch.Add(item);
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) { }

                if (batch.Count >= AlarmBatchSize || batch.Count > 0)
                {
                    await FlushAlarmBatchAsync(batch);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error(ex, "报警批量消费循环异常"); }
        }

        while (_alarmChannel.Reader.TryRead(out var remaining))
            batch.Add(remaining);

        if (batch.Count > 0)
            await FlushAlarmBatchAsync(batch);
    }

    private async Task FlushAlarmBatchAsync(List<AlarmRecordEntity> batch)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();
            using var transaction = connection.BeginTransaction();

            const string sql = """
            INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode, TriggerValue, QualityCode)
            VALUES (@DeviceId, @Timestamp, @AlarmCode, @TriggerValue, @QualityCode)
            """;

            await connection.ExecuteAsync(sql, batch, transaction: transaction);
            transaction.Commit();
            Log.Debug("批量写入 {Count} 条报警记录", batch.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量保存报警记录失败, 丢失 {Count} 条", batch.Count);
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
                new { From = from.ToString("O"), To = to.ToString("O") })).AsList();

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

                if (batch.Count >= TelemetryBatchSize || batch.Count > 0)
                {
                    await FlushTelemetryBatchAsync(batch);
                    batch.Clear();
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
            Log.Debug("批量写入 {Count} 条遥测记录", batch.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量保存遥测记录失败, 丢失 {Count} 条", batch.Count);
        }
    }

    // ======================== 数据库初始化 ========================

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
        _alarmChannel.Writer.Complete();
        _telemetryChannel.Writer.Complete();

        await _alarmCts.CancelAsync();
        await _telemetryCts.CancelAsync();

        try { await _alarmConsumerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }

        try { await _telemetryConsumerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }

        _alarmCts.Dispose();
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
