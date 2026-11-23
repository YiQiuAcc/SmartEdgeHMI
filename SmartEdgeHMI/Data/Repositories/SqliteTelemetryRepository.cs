using System.Threading.Channels;
using Dapper;
using Serilog;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Utils.Math;

namespace SmartEdgeHMI.Data.Repositories;

/// <summary>遥测持久化仓储。采集线程将数据写入有界队列，由后台消费者批量写入 SQLite。</summary>
/// <remarks>
/// 队列容量为 <c>BatchSize * 10</c> 且队列满时采用 <see cref="BoundedChannelFullMode.Wait"/>策略,
/// 写入方等待形成对采集线程的背压，不主动丢弃数据; 本类的写入路径不可在 UI 线程上长时间等待。
/// </remarks>
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
            // 接口无取消参数: 队列满时此处会一直等待消费者释放队列空间, 由背压保证不丢失样本
            await _channel.Writer.WriteAsync(entity);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "遥测记录入队失败");
        }
    }

    public async Task<List<SensorReadingRecord>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints)
    {
        if (to <= from)
            return [];

        try
        {
            await using var connection = await _factory.CreateConnectionAsync();

            // to 落在零点时视为"整天"语义, 扩展到当天最后一刻, 避免 <= 比较漏掉当天全部数据
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
            // 采样后条数仍超出时再执行一次 LTTB, 保证返回条数不超过 targetPoints(保持调用方语义)
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

            // 单条 INSERT 配 Dapper 列表参数, 由同一条语句展开为多行, 整批共用一个事务
            await connection.ExecuteAsync("""
                INSERT INTO TelemetryHistory (DeviceId, Timestamp, Temperature, Humidity, StatusCode, ErrorCode, QualityCode)
                VALUES (@DeviceId, @Timestamp, @Temperature, @Humidity, @StatusCode, @ErrorCode, @QualityCode)
                """, batch, transaction: transaction);

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            // 整批丢弃且不写回队列: 事务已回滚, 此处只能记录丢失条数
            Log.Error(ex, "批量保存遥测记录失败, 丢失 {Count} 条", batch.Count);
        }
    }

    /// <summary>停止消费者并尽力刷完队列中的残留数据</summary>
    /// <remarks>
    /// 顺序为先 Complete 让消费者读到通道结束, 再取消以打断最长 1s 的等待, 最后限时 5s 等待任务收尾。
    /// 未刷完的批次随进程退出丢失。
    /// </remarks>
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
