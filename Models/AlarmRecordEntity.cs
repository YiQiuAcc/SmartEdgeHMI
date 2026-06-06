namespace SmartEdgeHMI.Models.Entities;

/// <summary>对应 SQLite 数据库中的 AlarmHistory 表</summary>
public class AlarmRecordEntity
{
    public long Id { get; set; } // 数据库自增主键
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string AlarmCode { get; set; } = string.Empty;

    // 触发报警时的具体数值
    public double TriggerValue { get; set; }

    // UI 显示格式化属性 (WPF 绑定时直接调用)
    public string FormattedTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
}
