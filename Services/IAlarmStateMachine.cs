using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

public interface IAlarmStateMachine
{
    /// <summary>评估遥测载荷是否触发报警（边缘触发）</summary>
    AlarmRecordEntity? Evaluate(TelemetryPayload payload);

    /// <summary>当前活跃报警集合（Deviced → ErrorCode）</summary>
    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }
}
