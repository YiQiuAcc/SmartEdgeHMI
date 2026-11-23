using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Models.Dtos;

/// <summary>报警及其前后遥测窗口的组合结果, 供报警详情页绘制上下文曲线</summary>
public class AlarmWithTelemetry
{
    public AlarmRecord Alarm { get; init; } = null!;

    /// <summary>窗口内按时间升序排列的遥测样本(默认报警时刻 ±5 分钟)</summary>
    public List<SensorReadingRecord> SurroundingTelemetry { get; init; } = [];
}
