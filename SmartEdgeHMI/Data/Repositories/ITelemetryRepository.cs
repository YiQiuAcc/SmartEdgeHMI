using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Data.Repositories;

/// <summary>遥测数据持久化仓储</summary>
public interface ITelemetryRepository
{
    /// <summary>保存一条遥测记录</summary>
    Task SaveTelemetryAsync(SensorReadingRecord entity);

    /// <summary>查询指定时间范围内的遥测数据, 支持降采样到指定点数</summary>
    Task<List<SensorReadingRecord>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints);
}
