using SmartEdgeHMI.Models;
using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public interface IAlarmRepository
{
    Task<List<AlarmRecordEntity>> GetAlarmHistoryAsync(AlarmHistoryFilter? filter = null);

    Task SaveAlarmRecordAsync(AlarmRecordEntity alarmRecord);
}
