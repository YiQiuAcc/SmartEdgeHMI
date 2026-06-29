using SmartEdgeHMI.Common;
using SmartEdgeHMI.Database.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.MachineState;

/// <summary>ISA-18.2 报警状态机: 管理报警的未确认/已确认/恢复未确认/正常四种状态转换</summary>
public interface IAlarmStateMachine
{
    /// <summary>评估遥测数据并返回新产生的报警记录；无新报警时返回 null</summary>
    AlarmRecord? Evaluate(TelemetryPayload payload);

    /// <summary>当前活跃的报警集合(设备ID → 错误码)</summary>
    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }

    /// <summary>待处理的报警列表(未确认或未恢复)</summary>
    IReadOnlyList<AlarmRecord> PendingAlarms { get; }

    /// <summary>确认指定设备的所有报警</summary>
    void Acknowledge(string deviceId);

    /// <summary>确认所有未确认报警</summary>
    void AcknowledgeAll();

    /// <summary>报警状态变更通知</summary>
    event Action? AlarmStatesChanged;
}
