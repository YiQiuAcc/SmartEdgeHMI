using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public interface ISqliteRepository
{
    public List<AlarmRecordEntity> GetAlarmHistory();

    public void SaveAlarmRecord(AlarmRecordEntity alarmRecord);
}
