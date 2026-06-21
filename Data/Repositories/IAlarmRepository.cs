using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Data.Repositories;

public interface IAlarmRepository
{
    Task<List<AlarmRecordEntity>> GetAlarmHistoryAsync(AlarmHistoryFilter? filter = null);

    Task SaveAlarmRecordAsync(AlarmRecordEntity alarmRecord);
}
