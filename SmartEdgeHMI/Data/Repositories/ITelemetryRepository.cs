using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Data.Repositories;

public interface ITelemetryRepository
{
    Task SaveTelemetryAsync(SensorReadingRecord entity);

    Task<List<SensorReadingRecord>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints);
}
