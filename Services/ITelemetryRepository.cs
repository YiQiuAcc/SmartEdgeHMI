using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public interface ITelemetryRepository
{
    Task SaveTelemetryAsync(SensorReadingEntity entity);

    Task<List<SensorReadingEntity>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints);
}
