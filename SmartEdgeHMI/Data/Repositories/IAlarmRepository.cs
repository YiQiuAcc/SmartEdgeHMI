using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Data.Repositories;

public interface IAlarmRepository
{
    Task<List<AlarmRecord>> GetAlarmHistoryAsync(AlarmHistoryFilter? filter = null);

    Task<long> SaveAlarmRecordAsync(AlarmRecord alarmRecord);

    Task UpdateAlarmStatesAsync(IEnumerable<AlarmRecord> alarms);
}
