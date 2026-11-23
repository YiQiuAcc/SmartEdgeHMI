using Dapper;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace SmartEdgeHMI.Data.Retention;

/// <summary>数据保留策略: 遥测与报警分别按配置天数计算截止时间, 固定批大小循环删除</summary>
/// <remarks>
/// 单轮批数设安全上限, 防止海量积压时一次清理长时间占用连接; 配置 &lt;= 0 表示关闭对应表的清理。
/// 清理失败只记日志, 留待下一周期重试, 不影响主程序。
/// </remarks>
public sealed class DataRetentionService(SqliteConnectionFactory factory, IConfiguration config)
    : IDataRetentionService, IAsyncDisposable
{
    private const int BatchSize = 5000;
    private const int MaxBatchesPerRun = 200;
    private const int BatchGapMs = 50;
    private const int ShutdownTimeoutSeconds = 5;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private bool _disposed;

    // 配置缺失/非法时回退默认值
    private readonly int _retentionDays = int.TryParse(config["DatabaseSettings:RetentionDays"], out int days) ? days : 30;
    private readonly int _alarmRetentionDays = int.TryParse(config["DatabaseSettings:AlarmRetentionDays"], out int alarmDays) ? alarmDays : 0;
    private readonly int _checkIntervalHours = int.TryParse(config["DatabaseSettings:RetentionCheckIntervalHours"], out int hours) ? hours : 24;

    public async Task<RetentionResult> PurgeAsync(DateTime utcNow, CancellationToken ct = default)
    {
        // 两表均关闭时不碰数据库, 直接返回 0 行结果
        if (_retentionDays <= 0 && _alarmRetentionDays <= 0)
            return default;

        long telemetryDeleted = 0;
        long alarmDeleted = 0;
        bool batchLimitReached = false;

        // 两表相互隔离, 一张表清理失败不影响另一张表, 失败留待下一轮周期重试
        if (_retentionDays > 0)
        {
            try
            {
                (telemetryDeleted, bool telemetryHitLimit) =
                    await PurgeTableAsync("TelemetryHistory", utcNow - TimeSpan.FromDays(_retentionDays), ct);
                batchLimitReached |= telemetryHitLimit;
            }
            catch (Exception ex) { Log.Error(ex, "遥测保留清理失败(保留 {RetentionDays} 天)", _retentionDays); }
        }

        if (_alarmRetentionDays > 0)
        {
            try
            {
                (alarmDeleted, bool alarmHitLimit) =
                    await PurgeTableAsync("AlarmHistory", utcNow - TimeSpan.FromDays(_alarmRetentionDays), ct);
                batchLimitReached |= alarmHitLimit;
            }
            catch (Exception ex) { Log.Error(ex, "报警保留清理失败(保留 {AlarmRetentionDays} 天)", _alarmRetentionDays); }
        }

        // 删除 0 行时保持安静, 避免产生日志噪音
        if (telemetryDeleted > 0 || alarmDeleted > 0)
        {
            Log.Information(
                "数据保留清理完成: 遥测删除 {TelemetryDeleted} 行(保留 {RetentionDays} 天), 报警删除 {AlarmDeleted} 行(保留 {AlarmRetentionDays} 天), 批数上限截断: {BatchLimitReached}",
                telemetryDeleted, _retentionDays, alarmDeleted, _alarmRetentionDays, batchLimitReached);
        }

        return new RetentionResult(telemetryDeleted, alarmDeleted, batchLimitReached);
    }

    public void Start()
    {
        if (_disposed) return;

        // Start 幂等: 只允许启动一个后台循环
        _loopTask ??= Task.Run(() => RunLoopAsync(_cts.Token));
        Log.Information("数据保留策略已启动 (遥测保留 {RetentionDays} 天, 报警保留 {AlarmRetentionDays} 天, 检查周期 {CheckIntervalHours} 小时)",
            _retentionDays, _alarmRetentionDays, _checkIntervalHours);
    }

    /// <summary>后台清理循环: 立即清一次, 之后按配置周期重复, 异常只记日志</summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(DateTime.UtcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error(ex, "数据保留清理循环异常, 等待下一周期重试"); }

            try
            {
                await Task.Delay(TimeSpan.FromHours(_checkIntervalHours), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>单表分批删除: 每批 BatchSize 行, 影响行数为 0 时提前结束, 达到批数上限时停止并在结果中标记</summary>
    /// <remarks>批间 Delay 让出执行权, 降低与采集线程批量插入争抢写锁的持续压力</remarks>
    private async Task<(long Deleted, bool LimitReached)> PurgeTableAsync(string tableName, DateTime cutoff, CancellationToken ct)
    {
        long totalDeleted = 0;

        await using var connection = await factory.CreateConnectionAsync();

        for (int batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            ct.ThrowIfCancellationRequested();

            // 表名来自本类内的固定调用点, 非外部输入, 可安全拼入 SQL
            int deleted = await connection.ExecuteAsync($"""
                DELETE FROM {tableName}
                WHERE rowid IN (SELECT rowid FROM {tableName} WHERE Timestamp < @Cutoff LIMIT @Batch)
                """, new { Cutoff = cutoff, Batch = BatchSize });

            if (deleted == 0)
                return (totalDeleted, false);

            totalDeleted += deleted;
            await Task.Delay(BatchGapMs, ct);
        }

        return (totalDeleted, true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(ShutdownTimeoutSeconds)); }
            catch (OperationCanceledException)
            {
                // 正常取消, 任务已结束
            }
            catch (TimeoutException)
            {
                // 任务未能在指定时间内结束, 清理循环可能仍在删除中
            }
            catch (Exception ex) { Log.Warning(ex, "[Retention] 清理循环停止时抛出异常"); }
        }
        _cts.Dispose();
    }
}
