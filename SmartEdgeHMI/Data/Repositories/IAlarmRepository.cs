using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Filters;

namespace SmartEdgeHMI.Data.Repositories;

/// <summary>报警记录持久化仓储</summary>
public interface IAlarmRepository
{
    /// <summary>查询报警历史记录, 支持按时间范围和报警类型过滤</summary>
    /// <param name="filter">过滤条件, null 表示查询全部</param>
    Task<List<AlarmRecord>> GetAlarmHistoryAsync(AlarmHistoryFilter? filter = null);

    /// <summary>保存报警记录, 返回自增 ID</summary>
    Task<long> SaveAlarmRecordAsync(AlarmRecord alarmRecord);

    /// <summary>批量更新报警状态(如确认、恢复)</summary>
    Task UpdateAlarmStatesAsync(IEnumerable<AlarmRecord> alarms);
}
