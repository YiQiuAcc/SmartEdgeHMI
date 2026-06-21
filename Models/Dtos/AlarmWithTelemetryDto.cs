using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Models.Dtos;

/// <summary>报警记录及其相关的遥测数据上下文</summary>
public class AlarmWithTelemetryDto
{
    public AlarmRecordEntity Alarm { get; init; } = null!;
    public List<SensorReadingEntity> SurroundingTelemetry { get; init; } = [];
}
