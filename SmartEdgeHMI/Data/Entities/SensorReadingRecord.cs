using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;

namespace SmartEdgeHMI.Data.Entities;

/// <summary>遥测采样记录, 同时作为 TelemetryHistory 表的行模型与查询结果模型</summary>
/// <remarks>
/// <see cref="Temperature"/> 与 <see cref="Humidity"/> 由 <c>SqliteConnectionFactory</c> 注册的 Dapper TypeHandler
/// 映射为 REAL 列(单位 °C 与 %RH); 插入语句显式列出列名, Id 由 SQLite 自增。
/// </remarks>
public class SensorReadingRecord
{
    /// <summary>自增主键, 仅查询结果有值</summary>
    public long Id { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    /// <summary>采样时刻(本地时间)</summary>
    public DateTime Timestamp { get; set; }

    public Temperature Temperature { get; set; }

    public Humidity Humidity { get; set; }

    public DeviceStatus StatusCode { get; set; }

    public ErrorCode ErrorCode { get; set; }

    public DataQuality QualityCode { get; set; } = DataQuality.Good;
}
