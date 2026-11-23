using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Data.Repositories;

/// <summary>遥测数据持久化仓储</summary>
public interface ITelemetryRepository
{
    /// <summary>保存一条遥测记录</summary>
    /// <remarks>实现为异步入队; 队列满时调用方会被阻塞, 不应在 UI 线程上等待</remarks>
    Task SaveTelemetryAsync(SensorReadingRecord entity);

    /// <summary>查询指定时间范围内的遥测数据</summary>
    /// <param name="targetPoints">期望返回的点数上限, 超过则降采样; 小于 3 时返回原始行</param>
    Task<List<SensorReadingRecord>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints);
}
