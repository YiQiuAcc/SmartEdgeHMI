using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Data.Entities;

/// <summary>报警记录, 同时作为 AlarmHistory 表的行模型与状态机的内存载体</summary>
/// <remarks>
/// 持久化字段与属性一一对应, 改动属性名需同步 <c>SqliteAlarmRepository</c> 的 SQL 与迁移脚本。
/// <see cref="AlarmState"/> 以 INTEGER 持久化, 枚举成员不得重新编号, 否则历史数据的状态语义会整体偏移。
/// </remarks>
public class AlarmRecord
{
    /// <summary>自增主键, 未持久化前为 0</summary>
    public long Id { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    /// <summary>报警触发时刻(本地时间)</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>报警码, 取 <c>ErrorCode</c> 的枚举名持久化(TEXT), 便于阅读历史表</summary>
    public string AlarmCode { get; set; } = string.Empty;

    /// <summary>触发时的温度采样值, 单位 °C</summary>
    public double TriggerValue { get; set; }

    public DataQuality QualityCode { get; set; } = DataQuality.Good;

    public AlarmState State { get; set; } = AlarmState.UNACK;

    /// <summary>操作员确认时刻; 从 RTN_UNACK 重新触发时会被清空</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>恢复时刻; 仅在该记录首次回到正常态时写入</summary>
    public DateTime? ClearedAt { get; set; }
}
