using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Models.Entities;

/// <summary>对应 SQLite 数据库中的 AlarmHistory 表</summary>
public class AlarmRecordEntity
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string AlarmCode { get; set; } = string.Empty;

    /// <summary>触发报警时的具体数值</summary>
    public double TriggerValue { get; set; }

    /// <summary>OPC-UA 数据质量码：Good=可信, Uncertain=存疑, Bad=无效</summary>
    public DataQuality QualityCode { get; set; } = DataQuality.Good;
}
