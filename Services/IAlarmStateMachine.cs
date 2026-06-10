using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public interface IAlarmStateMachine
{
    AlarmRecordEntity? Evaluate(TelemetryPayload payload);
}
