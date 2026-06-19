using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public interface ISqliteRepository : IAsyncDisposable
{
    Task InitializeDatabaseAsync();

    Task<List<AlarmRecordEntity>> GetAlarmHistoryAsync();

    Task SaveAlarmRecordAsync(AlarmRecordEntity alarmRecord);

    Task SaveTelemetryAsync(SensorReadingEntity entity);

    Task<List<SensorReadingEntity>> GetTelemetryHistoryAsync(DateTime from, DateTime to, int targetPoints);
}
