using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public interface ISqliteRepository : IAsyncDisposable
{
    Task InitializeDatabaseAsync();

    Task<List<AlarmRecordEntity>> GetAlarmHistoryAsync();

    Task SaveAlarmRecordAsync(AlarmRecordEntity alarmRecord);
}
