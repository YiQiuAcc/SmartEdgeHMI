namespace SmartEdgeHMI.Data.Retention;

/// <summary>数据保留策略服务: 按配置天数分批清理过期遥测与报警历史, 防止数据库文件无限增长</summary>
public interface IDataRetentionService
{
    /// <summary>执行一轮清理, 删除超过保留天数的遥测/报警行</summary>
    /// <param name="utcNow">时间基准, 由调用方提供(后台循环传入 <see cref="DateTime.UtcNow"/>), 便于测试注入固定时刻</param>
    /// <returns>各表删除行数与是否因批数上限提前结束</returns>
    Task<RetentionResult> PurgeAsync(DateTime utcNow, CancellationToken ct = default);

    /// <summary>启动后台清理循环, 立即清理一次, 之后按 RetentionCheckIntervalHours 周期执行</summary>
    void Start();
}

/// <summary>单轮清理结果</summary>
/// <param name="BatchLimitReached">为 true 表示仍有超期数据未清完, 下一轮会继续</param>
public readonly record struct RetentionResult(long TelemetryDeleted, long AlarmDeleted, bool BatchLimitReached);
