using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.State;

public interface IAlarmStateMachine
{
    AlarmRecordEntity? Evaluate(TelemetryPayload payload);
    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }
}
