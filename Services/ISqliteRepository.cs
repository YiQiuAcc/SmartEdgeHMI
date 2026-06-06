using SmartEdgeHMI.Models;

namespace SmartEdgeHMI.Services;

public interface ISqliteRepository
{
    public List<AlarmHistory> GetAlarmHistory();
}
