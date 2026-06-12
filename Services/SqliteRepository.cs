using System.Data;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

public sealed class SqliteRepository : ISqliteRepository
{
    private readonly string _connectionString;
    private readonly int _queryLimit;

    private readonly Channel<AlarmRecordEntity> _channel;
    private readonly CancellationTokenSource _consumerCts;
    private readonly Task _consumerTask;

    private const int BatchSize = 50;
    private const int FlushIntervalMs = 1000;

    static SqliteRepository()
    {
        SqlMapper.AddTypeHandler(new DataQualityTypeHandler());
    }

    public SqliteRepository(IConfiguration config)
    {
        // 简化配置读取
        _connectionString = config.GetConnectionString("DefaultConnection") ?? "Data Source=SmartEdgeHMI.db";
        _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

        var options = new BoundedChannelOptions(BatchSize * 10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        };
        _channel = Channel.CreateBounded<AlarmRecordEntity>(options);
        _consumerCts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ConsumeAsync(_consumerCts.Token));
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
            await _channel.Writer.WriteAsync(alarmRecord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "报警记录入队失败");
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var batch = new List<AlarmRecordEntity>(BatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 使用 LinkedTokenSource 实现超时控制
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(FlushIntervalMs);

                try
                {
                    // 等待数据到达, 直到被主 CancellationToken 取消, 或达到超时时间
                    if (await _channel.Reader.WaitToReadAsync(timeoutCts.Token))
                    {
                        while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                        {
                            batch.Add(item);
                        }
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) { }

                // 批次已满, 或者有积压数据且超时触发
                if (batch.Count >= BatchSize || batch.Count > 0)
                {
                    await FlushBatchAsync(batch);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // 主程序请求关闭
            }
            catch (Exception ex)
            {
                Log.Error(ex, "批量消费循环异常");
            }
        }

        // 排空 Channel 中剩余数据
        while (_channel.Reader.TryRead(out var remaining))
            batch.Add(remaining);

        if (batch.Count > 0)
            await FlushBatchAsync(batch);
    }

    private async Task FlushBatchAsync(List<AlarmRecordEntity> batch)
    {
        try
        {
            await using var connection = await CreateConnectionAsync();
            using var transaction = connection.BeginTransaction();

            string sql = """
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

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        await _consumerCts.CancelAsync();

        try
        {
            // 非阻塞等待消费者任务完成, 设置 5 秒超时
            await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { }

        _consumerCts.Dispose();
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
