using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Core.Domain.MachineState;

/// <summary>ISA-18.2 报警状态机: UNACK / ACK / RTN_UNACK / NORMAL 四态迁移与恢复去抖</summary>
/// <remarks>
/// <c>_syncRoot</c> 保护 <c>_activeAlarms</c> 字典及其中 <see cref="AlarmContext.Record"/> 的可变字段。
/// 所有公开成员可从任意线程调用; <see cref="AlarmStatesChanged"/> 在锁外触发,
/// 处理器若反向调用本类型的加锁成员不会死锁, 但会看到已提交的中间状态。
/// </remarks>
public class AlarmStateMachine(ISettingsService settingsService) : IAlarmStateMachine
{
    private sealed class AlarmContext
    {
        public AlarmRecord Record { get; set; } = null!;

        // 连续正常读数的计数, 达到 RecoveryDebounceCount 才判定恢复, 用于滤掉单次抖动
        public int RecoveryCount;
    }

    private readonly Dictionary<string, AlarmContext> _activeAlarms = [];
    private readonly object _syncRoot = new();

    public event Action? AlarmStatesChanged;

    public IReadOnlyDictionary<string, ErrorCode> ActiveAlarms
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeAlarms
                    .Where(kv => kv.Value.Record.State != AlarmState.NORMAL)
                    .ToDictionary(kv => kv.Key, kv => Enum.Parse<ErrorCode>(kv.Value.Record.AlarmCode));
            }
        }
    }

    public IReadOnlyList<AlarmRecord> PendingAlarms
    {
        get
        {
            lock (_syncRoot)
            {
                // 字典里保留的记录都视为待处理: 事件窗口内刚转为 NORMAL 的记录也要能被取到,
                // 供 MonitorViewModel 把最终态同步进数据库; 事件返回后由 NotifyStatesChanged 移除
                return [.. _activeAlarms.Values.Select(c => c.Record)];
            }
        }
    }

    public AlarmRecord? Evaluate(TelemetryPayload payload)
    {
        if (payload.QualityCode == DataQuality.Bad)
            return null;

        lock (_syncRoot)
        {
            bool hasError = payload.ErrorCode != ErrorCode.NoError;
            _activeAlarms.TryGetValue(payload.DeviceId, out var ctx);

            if (hasError && ctx is null)
            {
                var record = CreateRecord(payload);
                _activeAlarms[payload.DeviceId] = new AlarmContext { Record = record };
                Log.Information("[ISA-18.2] {Device} 报警触发 UNACK: {Code}", payload.DeviceId, payload.ErrorCode);
                AlarmStatesChanged?.Invoke();
                return record;
            }

            if (hasError && ctx is not null)
            {
                ctx.RecoveryCount = 0;

                if (ctx.Record.State == AlarmState.RTN_UNACK)
                {
                    // 恢复后再次报错: 之前的确认与恢复时刻均已失效, 回到 UNACK 要求重新确认
                    ctx.Record.State = AlarmState.UNACK;
                    ctx.Record.AcknowledgedAt = null;
                    ctx.Record.ClearedAt = null;
                    Log.Information("[ISA-18.2] {Device} 重新报警 RTN_UNACK→UNACK", payload.DeviceId);
                    AlarmStatesChanged?.Invoke();
                }
                return null;
            }

            if (!hasError && ctx is not null)
            {
                ctx.RecoveryCount++;

                if (ctx.RecoveryCount >= settingsService.Current.Alarm.RecoveryDebounceCount)
                {
                    TransitionToRecovered(payload.DeviceId, ctx);
                }
            }

            return null;
        }
    }

    private void TransitionToRecovered(string deviceId, AlarmContext ctx)
    {
        switch (ctx.Record.State)
        {
            case AlarmState.UNACK:
                ctx.Record.State = AlarmState.RTN_UNACK;
                ctx.Record.ClearedAt = DateTime.Now;
                Log.Information("[ISA-18.2] {Device} 恢复 UNACK→RTN_UNACK", deviceId);
                break;
            case AlarmState.ACK:
                ctx.Record.State = AlarmState.NORMAL;
                ctx.Record.ClearedAt = DateTime.Now;
                Log.Information("[ISA-18.2] {Device} 完全恢复 ACK→NORMAL", deviceId);
                break;
        }
        NotifyStatesChanged();
    }

    /// <summary>触发状态变化事件, 事件返回后再把已完全恢复(NORMAL)的记录移出活动表</summary>
    /// <remarks>
    /// 顺序不可调换: 处理器(UI 刷新、落库)必须仍能从 <see cref="PendingAlarms"/> 取到带 ClearedAt 的最终态记录。
    /// </remarks>
    private void NotifyStatesChanged()
    {
        try
        {
            AlarmStatesChanged?.Invoke();
        }
        finally
        {
            // 移除放在 finally, 处理器抛异常时也不会残留已终结的报警
            lock (_syncRoot)
            {
                var settled = _activeAlarms
                    .Where(kv => kv.Value.Record.State == AlarmState.NORMAL)
                    .Select(kv => kv.Key).ToList();
                foreach (string key in settled)
                    _activeAlarms.Remove(key);
            }
        }
    }

    public void Acknowledge(string deviceId)
    {
        lock (_syncRoot)
        {
            if (!_activeAlarms.TryGetValue(deviceId, out var ctx)) return;
            ApplyAck(deviceId, ctx);
        }
        NotifyStatesChanged();
    }

    public void AcknowledgeAll()
    {
        int count;
        lock (_syncRoot)
        {
            count = _activeAlarms.Values.Count(c => c.Record.State is AlarmState.UNACK or AlarmState.RTN_UNACK);

            foreach (var (key, ctx) in _activeAlarms)
            {
                if (ctx.Record.State is AlarmState.UNACK or AlarmState.RTN_UNACK)
                    ApplyAck(key, ctx);
            }
        }
        Log.Information("[ISA-18.2] 一键确认完成: 处理 {Count} 条报警", count);
        NotifyStatesChanged();
    }

    private static void ApplyAck(string deviceId, AlarmContext ctx)
    {
        switch (ctx.Record.State)
        {
            case AlarmState.UNACK:
                ctx.Record.State = AlarmState.ACK;
                ctx.Record.AcknowledgedAt = DateTime.Now;
                Log.Information("[ISA-18.2] {Device} 操作员确认 UNACK→ACK", deviceId);
                break;
            case AlarmState.RTN_UNACK:
                ctx.Record.State = AlarmState.NORMAL;
                ctx.Record.AcknowledgedAt ??= DateTime.Now;
                Log.Information("[ISA-18.2] {Device} 操作员确认 RTN_UNACK→NORMAL", deviceId);
                break;
        }
    }

    private static AlarmRecord CreateRecord(TelemetryPayload payload) => new()
    {
        DeviceId = payload.DeviceId,
        Timestamp = DateTime.Now,
        AlarmCode = payload.ErrorCode.ToString(),
        TriggerValue = payload.Temperature.Celsius,
        QualityCode = payload.QualityCode,
        State = AlarmState.UNACK
    };
}
