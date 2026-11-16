using System.Threading.Channels;
using Dapper;
using Serilog;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Utils.Math;

namespace SmartEdgeHMI.Data.Repositories;

/// <summary>遥测持久化仓储: 双缓冲 Channel 模型, 采集线程仅入队, 后台消费者按批次/时间合并刷盘。</summary>
public sealed class SqliteTelemetryRepository : ITelemetryRepository, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Channel<SensorReadingRecord> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _consumerTask;
    private bool _disposed;

    private const int BatchSize = 50;
    private const int FlushIntervalMs = 1_000;

    public SqliteTelemetryRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
        _channel = Channel.CreateBounded<SensorReadingRecord>(new BoundedChannelOptions(BatchSize * 10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });
        _cts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ConsumeAsync(_cts.Token));
    }

    public async Task SaveTelemetryAsync(SensorReadingRecord entity)
    {
        try
        {
            await _channel.Writer.WriteAsync(entity);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "遥测记录入队失败");
        }
    }

    public async Task<List<SensorReadingRecord>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints)
    {
        // 空区间无数据可查, 直接返回空列表
        if (to <= from)
            return [];

        try
        {
            await using var connection = await _factory.CreateConnectionAsync();

            DateTime toInclusive = to.Date.Equals(to) ? to.Date.AddDays(1).AddTicks(-1) : to;

            // LTTB 至少需要 3 个点, targetPoints < 3 时退化为原始查询: 范围内全部行, 不降采样
            if (targetPoints < 3)
            {
                return [.. await connection.QueryAsync<SensorReadingRecord>("""
                    SELECT * FROM TelemetryHistory
                    WHERE Timestamp >= @From AND Timestamp <= @To
                    ORDER BY Timestamp ASC
                    """, new { From = from, To = toInclusive })];
            }

            // 桶宽公式: 查询区间总秒数均分到 targetPoints 个桶, 下限 1 秒避免桶宽小于采样间隔
            double bucketSeconds = Math.Max(1, (to - from).TotalSeconds / targetPoints);

            // 每桶取温度最小值和最大值所在的两个真实采样点(而非聚合均值), 用于保留尖峰不被平均抹平;
            // 桶序号 = floor((行时间 - @From) 折算秒数 / 桶宽), 过滤与取样全部下推 SQL, 只回传采样点行
            var sampled = (await connection.QueryAsync<SensorReadingRecord>("""
                WITH Bucketed AS (
                    SELECT Timestamp, DeviceId, Temperature, Humidity, StatusCode, ErrorCode, QualityCode,
                           CAST((julianday(Timestamp) - julianday(@From)) * 86400.0 / @BucketSeconds AS INTEGER) AS Bucket
                    FROM TelemetryHistory
                    WHERE Timestamp >= @From AND Timestamp <= @To
                ),
                Ranked AS (
                    SELECT *, ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY Temperature ASC)  AS MinRank,
                              ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY Temperature DESC) AS MaxRank
                    FROM Bucketed
                )
                SELECT Timestamp, DeviceId, Temperature, Humidity, StatusCode, ErrorCode, QualityCode
                FROM Ranked WHERE MinRank = 1 OR MaxRank = 1
                ORDER BY Timestamp ASC
                """, new { From = from, To = toInclusive, BucketSeconds = bucketSeconds })).AsList();

            // 每桶最多 2 行, 但整日扩展(toInclusive)等因素会让实际桶数略多于 targetPoints;
            // 采样后条数仍超出时再走一次 LTTB, 保证返回条数不超过 targetPoints(保持调用方语义)
            return sampled.Count > targetPoints
                ? LttbDownsampler.Downsample(sampled, targetPoints)
                : sampled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取遥测历史失败");
            return [];
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var batch = new List<SensorReadingRecord>(BatchSize);
        var lastFlush = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await FillBatchAsync(batch, ct);
                lastFlush = await FlushIfNeededAsync(batch, lastFlush);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error(ex, "遥测批量消费循环异常"); }
        }

        await FlushRemainingAsync(batch);
    }

    private async Task FillBatchAsync(List<SensorReadingRecord> batch, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FlushIntervalMs);

        try
        {
            if (await _channel.Reader.WaitToReadAsync(timeoutCts.Token))
            {
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                    batch.Add(item);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 正常超时, 触发 Flush
        }
    }

    private async Task<DateTime> FlushIfNeededAsync(List<SensorReadingRecord> batch, DateTime lastFlush)
    {
        bool countReached = batch.Count >= BatchSize;
        bool timeReached = batch.Count > 0 && (DateTime.UtcNow - lastFlush).TotalMilliseconds >= FlushIntervalMs;

        if (countReached || timeReached)
        {
            await FlushBatchAsync(batch);
            batch.Clear();
            return DateTime.UtcNow;
        }

        return lastFlush;
    }

    private async Task FlushRemainingAsync(List<SensorReadingRecord> batch)
    {
        while (_channel.Reader.TryRead(out var remaining))
            batch.Add(remaining);

        if (batch.Count > 0)
            await FlushBatchAsync(batch);
    }

    private async Task FlushBatchAsync(List<SensorReadingRecord> batch)
    {
        try
        {
            await using var connection = await _factory.CreateConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await connection.ExecuteAsync("""
                INSERT INTO TelemetryHistory (DeviceId, Timestamp, Temperature, Humidity, StatusCode, ErrorCode, QualityCode)
                VALUES (@DeviceId, @Timestamp, @Temperature, @Humidity, @StatusCode, @ErrorCode, @QualityCode)
                """, batch, transaction: transaction);

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量保存遥测记录失败, 丢失 {Count} 条", batch.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.Complete();
        await _cts.CancelAsync();
        try { await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException)
        {
            // 正常取消, 任务已结束
        }
        catch (TimeoutException)
        {
            // 任务未能在指定时间内结束, 可能仍在处理剩余数据
        }
        catch (Exception ex) { Log.Warning(ex, "[TelemetryRepo] 消费任务停止时抛出异常"); }
        _cts.Dispose();
    }
}
