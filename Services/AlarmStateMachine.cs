using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

/// <summary>边缘触发报警状态机：仅在状态转换瞬间记录，防止报警风暴</summary>
public class AlarmStateMachine : IAlarmStateMachine
{
    private readonly Dictionary<string, ErrorCode> _activeAlarms = [];
    private readonly Dictionary<string, int> _recoveryCounters = [];
    private readonly object _syncRoot = new();

    public IReadOnlyDictionary<string, ErrorCode> ActiveAlarms
    {
        get
        {
            lock (_syncRoot)
            {
                return new Dictionary<string, ErrorCode>(_activeAlarms);
            }
        }
    }

    public AlarmRecordEntity? Evaluate(TelemetryPayload payload)
    {
        if (payload.QualityCode == DataQuality.Bad)
            return null;

        lock (_syncRoot)
        {
            bool hasError = payload.ErrorCode != ErrorCode.NoError;
            bool isCurrentlyAlarmed = _activeAlarms.ContainsKey(payload.DeviceId);

            if (hasError && !isCurrentlyAlarmed)
            {
                // ↑ 上升沿: Normal → Alarm
                _activeAlarms[payload.DeviceId] = payload.ErrorCode;
                _recoveryCounters.Remove(payload.DeviceId);
                return CreateRecord(payload);
            }
            else if (!hasError && isCurrentlyAlarmed)
            {
                // ↓ 潜在下降沿: 进入恢复迟滞计数
                if (!_recoveryCounters.TryGetValue(payload.DeviceId, out int count))
                    count = 0;
                count++;
                _recoveryCounters[payload.DeviceId] = count;

                if (count >= AppConstants.AlarmRecoveryDebounceCount)
                {
                    _activeAlarms.Remove(payload.DeviceId);
                    _recoveryCounters.Remove(payload.DeviceId);
                    Log.Information("报警恢复: {DeviceId}, 连续 {Count} 帧正常", payload.DeviceId, count);
                }
            }
            else if (hasError && isCurrentlyAlarmed)
            {
                // → 报警持续: 重置恢复计数器，防止瞬时正常被误判为恢复
                _recoveryCounters.Remove(payload.DeviceId);
            }
            // else: !hasError && !isCurrentlyAlarmed → 稳态正常，无需处理

            return null;
        }
    }

    private static AlarmRecordEntity CreateRecord(TelemetryPayload payload) => new()
    {
        DeviceId = payload.DeviceId,
        Timestamp = DateTime.Now,
        AlarmCode = payload.ErrorCode.ToString(),
        TriggerValue = payload.Temperature.Celsius,
        QualityCode = payload.QualityCode
    };
}
