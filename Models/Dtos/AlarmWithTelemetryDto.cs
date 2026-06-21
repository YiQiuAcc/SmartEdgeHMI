using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Models.Dtos;

public class AlarmWithTelemetryDto
{
    public AlarmRecordEntity Alarm { get; init; } = null!;
    public List<SensorReadingEntity> SurroundingTelemetry { get; init; } = [];
}
