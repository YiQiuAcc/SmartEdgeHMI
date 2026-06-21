using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Data.Repositories;

public interface ITelemetryRepository
{
    Task SaveTelemetryAsync(SensorReadingEntity entity);

    Task<List<SensorReadingEntity>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints);
}
