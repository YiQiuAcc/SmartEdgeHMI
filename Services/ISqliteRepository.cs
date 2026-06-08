using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public interface ISqliteRepository
{
    Task InitializeDatabaseAsync();

    Task<List<AlarmRecordEntity>> GetAlarmHistoryAsync();

    Task SaveAlarmRecordAsync(AlarmRecordEntity alarmRecord);
}
