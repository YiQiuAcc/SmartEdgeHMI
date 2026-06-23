using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.State;

public class AlarmStateMachine : IAlarmStateMachine
{
    private sealed class AlarmContext
    {
        public AlarmRecord Record { get; set; } = null!;
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
                return [.. _activeAlarms.Values
                    .Where(c => c.Record.State != AlarmState.NORMAL)
                    .Select(c => c.Record)];
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

                if (ctx.RecoveryCount >= AppConstants.AlarmRecoveryDebounceCount)
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
                _activeAlarms.Remove(deviceId);
                Log.Information("[ISA-18.2] {Device} 完全恢复 ACK→NORMAL", deviceId);
                break;
        }
        AlarmStatesChanged?.Invoke();
    }

    public void Acknowledge(string deviceId)
    {
        lock (_syncRoot)
        {
            if (!_activeAlarms.TryGetValue(deviceId, out var ctx)) return;
            ApplyAck(deviceId, ctx);
        }
        AlarmStatesChanged?.Invoke();
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

            var removed = _activeAlarms
                .Where(kv => kv.Value.Record.State == AlarmState.NORMAL)
                .Select(kv => kv.Key).ToList();
            foreach (string? key in removed)
                _activeAlarms.Remove(key);
        }
        Log.Information("[ISA-18.2] 一键确认完成: 处理 {Count} 条报警", count);
        AlarmStatesChanged?.Invoke();
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
