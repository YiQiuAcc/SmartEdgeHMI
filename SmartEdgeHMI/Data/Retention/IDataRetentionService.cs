namespace SmartEdgeHMI.Data.Retention;

/// <summary>数据保留策略服务: 按配置天数分批清理过期遥测与报警历史, 防止边缘设备库文件无限增长。</summary>
public interface IDataRetentionService
{
    /// <summary>按传入时间执行一轮清理: 删除超过保留天数的遥测/报警行, 返回本次删除行数汇总。</summary>
    Task<RetentionResult> PurgeAsync(DateTime utcNow, CancellationToken ct = default);

    /// <summary>启动后台清理循环: 立即执行一次清理, 之后按 RetentionCheckIntervalHours 周期执行。</summary>
    void Start();
}

/// <summary>单轮清理结果: 各表删除行数与是否因批数安全上限提前结束。</summary>
public readonly record struct RetentionResult(long TelemetryDeleted, long AlarmDeleted, bool BatchLimitReached);
