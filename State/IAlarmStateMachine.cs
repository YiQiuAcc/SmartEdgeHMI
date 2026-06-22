using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.State;

public interface IAlarmStateMachine
{
    AlarmRecord? Evaluate(TelemetryPayload payload);
    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }
    IReadOnlyList<AlarmRecord> PendingAlarms { get; }

    void Acknowledge(long alarmId);

    void AcknowledgeAll();

    event Action? AlarmStatesChanged;
}
