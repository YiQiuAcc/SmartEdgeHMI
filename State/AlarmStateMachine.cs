using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.State;

/// <summary>
/// 边缘触发报警状态机：仅在 Normal→Alarm 的上升沿产生报警记录, 防止报警风暴。 从 Alarm→Normal 需连续 N
/// 帧(AlarmRecoveryDebounceCount)均无错误才判定恢复, 期间任一帧再次触发报警将重置恢复计数器 —— 实现恢复迟滞(Hysteresis)。
/// </summary>
public class AlarmStateMachine : IAlarmStateMachine
{
    /// <summary>当前活跃报警集合(DeviceId → ErrorCode), 供外部同步 UI 状态</summary>
    private readonly Dictionary<string, ErrorCode> _activeAlarms = [];

    /// <summary>
    /// 恢复计数器：DeviceId → 连续正常帧数。 当连续正常帧达到 AlarmRecoveryDebounceCount 后, 才判定设备从报警中恢复。
    /// </summary>
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

    /// <summary>
    /// 评估遥测数据, 若发生上升沿报警则返回报警记录, 否则返回 null。
    ///
    /// 状态转换规则： ↑ 上升沿(Normal→Alarm)：立即产生报警记录 → 持续报警(Alarm→Alarm)：重置恢复计数器, 防止瞬时正常误判恢复 ↓
    /// 下降沿(Alarm→Normal)：递增恢复计数器, 达阈值才算真正恢复
    /// - 稳态(Normal→Normal)：跳过 Bad 质量码的数据直接忽略(数据不可信, 不触发任何状态变更)
    /// </summary>
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
                // ↑ 上升沿：Normal → Alarm, 产生记录
                _activeAlarms[payload.DeviceId] = payload.ErrorCode;
                _recoveryCounters.Remove(payload.DeviceId);
                return CreateRecord(payload);
            }
            else if (!hasError && isCurrentlyAlarmed)
            {
                // ↓ 潜在下降沿：递增恢复计数器, 累积达阈值才判定恢复
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
                // → 报警持续中：重置恢复计数器, 防瞬时正常误判
                _recoveryCounters.Remove(payload.DeviceId);
            }
            // else: !hasError && !isCurrentlyAlarmed → 稳态正常, 跳过

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
