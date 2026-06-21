using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.State;

public interface IAlarmStateMachine
{
    AlarmRecord? Evaluate(TelemetryPayload payload);
    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }
}
