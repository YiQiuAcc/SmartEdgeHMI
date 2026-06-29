using SmartEdgeHMI.Database.Entities;

namespace SmartEdgeHMI.Models.Dtos;

public class AlarmWithTelemetry
{
    public AlarmRecord Alarm { get; init; } = null!;
    public List<SensorReadingRecord> SurroundingTelemetry { get; init; } = [];
}
