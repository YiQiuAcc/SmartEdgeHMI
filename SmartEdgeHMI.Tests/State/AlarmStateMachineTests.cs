using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.ValueObjects;
using SmartEdgeHMI.State;

namespace SmartEdgeHMI.Tests.State;

public class AlarmStateMachineTests
{
    #region 基础功能测试

    /// <summary>正常遥测数据不应触发报警</summary>
    [Fact]
    public void Evaluate_NormalPayload_ShouldReturnNull()
    {
        var machine = new AlarmStateMachine();
        var payload = CreateNormalPayload("Sensor_01");

        var result = machine.Evaluate(payload);

        Assert.Null(result);
    }

    /// <summary>Bad Quality 遥测数据不应触发报警</summary>
    [Fact]
    public void Evaluate_BadQuality_ShouldReturnNull()
    {
        var machine = new AlarmStateMachine();
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.NoError, DataQuality.Bad);

        var result = machine.Evaluate(payload);

        Assert.Null(result);
    }

    /// <summary>首次收到错误码应触发报警, 返回 UNACK 状态的 AlarmRecord</summary>
    [Fact]
    public void Evaluate_FirstError_ShouldTriggerAlarm_UNACK()
    {
        var machine = new AlarmStateMachine();
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);

        var result = machine.Evaluate(payload);

        Assert.NotNull(result);
        Assert.Equal("Sensor_01", result.DeviceId);
        Assert.Equal(ErrorCode.ThresholdExceeded.ToString(), result.AlarmCode);
        Assert.Equal(AlarmState.UNACK, result.State);
        Assert.Null(result.AcknowledgedAt);
        Assert.Null(result.ClearedAt);
    }

    /// <summary>相同错误连续上报不应重复创建报警记录</summary>
    [Fact]
    public void Evaluate_SameErrorRepeated_ShouldNotCreateNewAlarm()
    {
        var machine = new AlarmStateMachine();
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);

        var firstResult = machine.Evaluate(payload);
        var secondResult = machine.Evaluate(payload);

        Assert.NotNull(firstResult);
        Assert.Null(secondResult); // 重复错误不应产生新记录
    }

    /// <summary>在报警未确认的状态下错误消失, 应进入 RTN_UNACK 状态(恢复未确认)</summary>
    [Fact]
    public void Evaluate_ErrorCleared_UnackAlarm_ShouldTransitionToRTN_UNACK()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload); // 触发报警

        // 发送多次正常数据, 达到防抖阈值 (AppConstants.AlarmRecoveryDebounceCount = 3)
        var normalPayload = CreateNormalPayload("Sensor_01");
        AlarmRecord? result = null;
        for (int i = 0; i < 3; i++)
        {
            result = machine.Evaluate(normalPayload);
        }

        // 最后一次 Evaluate 应驱动状态变迁, 但返回 null(因为不是新报警)
        Assert.Null(result);

        // 验证报警状态已变迁到 RTN_UNACK
        var pendingAlarms = machine.PendingAlarms;
        var alarm = Assert.Single(pendingAlarms);
        Assert.Equal(AlarmState.RTN_UNACK, alarm.State);
        Assert.NotNull(alarm.ClearedAt);
    }

    #endregion 基础功能测试

    #region ISA-18.2 状态机转移测试

    /// <summary>UNACK → ACK:操作员确认报警后应进入 ACK 状态</summary>
    [Fact]
    public void Acknowledge_UnacknowledgedAlarm_ShouldTransitionToAck()
    {
        var machine = new AlarmStateMachine();
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        var alarm = machine.Evaluate(payload);
        Assert.NotNull(alarm);

        machine.Acknowledge(alarm.DeviceId);

        var pendingAlarms = machine.PendingAlarms;
        var acked = Assert.Single(pendingAlarms);
        Assert.Equal(AlarmState.ACK, acked.State);
        Assert.NotNull(acked.AcknowledgedAt);
    }

    /// <summary>RTN_UNACK → NORMAL:恢复未确认状态下确认应转为 NORMAL(报警消除)</summary>
    [Fact]
    public void Acknowledge_RTN_UNACK_ShouldTransitionToNormal()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        // 恢复(连续正常数据达到防抖阈值)
        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        // 确认 RTN_UNACK 状态
        var pendingBeforeAck = machine.PendingAlarms;
        var rtnUnackAlarm = Assert.Single(pendingBeforeAck);
        Assert.Equal(AlarmState.RTN_UNACK, rtnUnackAlarm.State);

        // 确认后应变为 NORMAL
        machine.Acknowledge(rtnUnackAlarm.DeviceId);

        // NORMAL 状态的报警不应在 PendingAlarms 中
        Assert.Empty(machine.PendingAlarms);
    }

    /// <summary>ACK → NORMAL:已确认后恢复应转为 NORMAL</summary>
    [Fact]
    public void Acknowledge_ThenRecover_ShouldTransitionToNormal()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        var alarm = machine.Evaluate(errorPayload);
        Assert.NotNull(alarm);

        // 确认
        machine.Acknowledge(alarm.DeviceId);

        // 恢复(连续正常数据达到防抖阈值)
        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        // ACK 状态恢复后应变为 NORMAL, 从 PendingAlarms 中移除
        Assert.Empty(machine.PendingAlarms);
    }

    /// <summary>RTN_UNACK → UNACK:恢复未确认状态下重新报警应回到 UNACK</summary>
    [Fact]
    public void Reactivate_RTN_UNACK_ShouldTransitionBackToUNACK()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        // 恢复
        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        // 验证 RTN_UNACK
        Assert.Single(machine.PendingAlarms);
        Assert.Equal(AlarmState.RTN_UNACK, machine.PendingAlarms[0].State);

        // 重新报警
        machine.Evaluate(errorPayload);

        // 应回到 UNACK
        var alarms = machine.PendingAlarms;
        var alarm = Assert.Single(alarms);
        Assert.Equal(AlarmState.UNACK, alarm.State);
        Assert.Null(alarm.ClearedAt); // ClearedAt 应被清除
    }

    #endregion ISA-18.2 状态机转移测试

    #region 一键确认测试

    /// <summary>AcknowledgeAll 应确认所有未确认和恢复未确认的报警</summary>
    [Fact]
    public void AcknowledgeAll_MultipleAlarms_ShouldAcknowledgeAll()
    {
        var machine = new AlarmStateMachine();

        // 创建多个设备的报警
        var alarm1 = machine.Evaluate(CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded));
        var alarm2 = machine.Evaluate(CreatePayload("Sensor_02", DeviceStatus.Online, ErrorCode.SensorDisconnected));
        var alarm3 = machine.Evaluate(CreatePayload("Sensor_03", DeviceStatus.Online, ErrorCode.PowerLow));

        Assert.NotNull(alarm1);
        Assert.NotNull(alarm2);
        Assert.NotNull(alarm3);

        // 恢复其中一个
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_03"));
        }

        // 一键确认
        machine.AcknowledgeAll();

        // Sensor_01 和 Sensor_02 应为 ACK(仍在报警中但已确认) Sensor_03 应为 NORMAL(已恢复并确认后移除)
        var pending = machine.PendingAlarms;
        Assert.Equal(2, pending.Count); // 只有 Sensor_01 和 Sensor_02 仍在报警中

        var ack1 = pending.First(a => a.DeviceId == "Sensor_01");
        var ack2 = pending.First(a => a.DeviceId == "Sensor_02");
        Assert.Equal(AlarmState.ACK, ack1.State);
        Assert.Equal(AlarmState.ACK, ack2.State);
        Assert.NotNull(ack1.AcknowledgedAt);
        Assert.NotNull(ack2.AcknowledgedAt);
    }

    /// <summary>没有报警时一键确认不应抛出异常</summary>
    [Fact]
    public void AcknowledgeAll_NoAlarms_ShouldNotThrow()
    {
        var machine = new AlarmStateMachine();

        var ex = Record.Exception(() => machine.AcknowledgeAll());
        Assert.Null(ex);
    }

    #endregion 一键确认测试

    #region 边缘场景测试

    /// <summary>防抖计数功能验证:恢复信号需要连续 N 次才能触发状态变更</summary>
    [Fact]
    public void RecoveryDebounce_ShouldRequireMultipleNormalReadings()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        var normalPayload = CreateNormalPayload("Sensor_01");

        // 第 1 次正常数据:不应触发恢复
        machine.Evaluate(normalPayload);
        var pending1 = machine.PendingAlarms;
        Assert.Equal(AlarmState.UNACK, pending1[0].State);

        // 第 2 次正常数据:仍不应触发恢复
        machine.Evaluate(normalPayload);
        Assert.Equal(AlarmState.UNACK, pending1[0].State);

        // 第 3 次正常数据(达到防抖阈值 3):应触发恢复
        machine.Evaluate(normalPayload);
        var pending2 = machine.PendingAlarms;
        Assert.Equal(AlarmState.RTN_UNACK, pending2[0].State);
    }

    /// <summary>消除报警后重新报警应重新创建报警记录</summary>
    [Fact]
    public void FullCycle_AlarmRecovered_ThenNewAlarm_ShouldCreateNewRecord()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        var alarm1 = machine.Evaluate(errorPayload);
        Assert.NotNull(alarm1);

        // 确认并恢复
        machine.Acknowledge(alarm1.DeviceId);
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_01"));
        }

        // 报警应完全消除
        Assert.Empty(machine.PendingAlarms);

        // 重新报警: 应创建新记录
        var alarm2 = machine.Evaluate(errorPayload);
        Assert.NotNull(alarm2);
        Assert.Equal(AlarmState.UNACK, alarm2.State);
        Assert.NotSame(alarm1, alarm2);
    }

    /// <summary>ActiveAlarms 只返回非 NORMAL 状态的报警</summary>
    [Fact]
    public void ActiveAlarms_ShouldNotIncludeNormalState()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        // 应该包含在 ActiveAlarms 中
        Assert.Single(machine.ActiveAlarms);
        Assert.True(machine.ActiveAlarms.ContainsKey("Sensor_01"));

        // 确认并恢复
        var alarm = machine.PendingAlarms[0];
        machine.Acknowledge(alarm.DeviceId);
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_01"));
        }

        // NORMAL 状态不应在 ActiveAlarms 中
        Assert.Empty(machine.ActiveAlarms);
    }

    /// <summary>多个不同设备的报警应独立管理</summary>
    [Fact]
    public void MultipleDevices_AlarmsShouldBeIndependent()
    {
        var machine = new AlarmStateMachine();

        machine.Evaluate(CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded));
        machine.Evaluate(CreatePayload("Sensor_02", DeviceStatus.Online, ErrorCode.SensorDisconnected));

        Assert.Equal(2, machine.PendingAlarms.Count);

        // 确认 Sensor_01 不影响 Sensor_02
        var alarm1 = machine.PendingAlarms.First(a => a.DeviceId == "Sensor_01");
        machine.Acknowledge(alarm1.DeviceId);

        Assert.Equal(AlarmState.ACK, machine.PendingAlarms.First(a => a.DeviceId == "Sensor_01").State);
        Assert.Equal(AlarmState.UNACK, machine.PendingAlarms.First(a => a.DeviceId == "Sensor_02").State);
    }

    /// <summary>确认不存在的 AlarmId 不应影响状态</summary>
    [Fact]
    public void Acknowledge_NonExistentAlarmId_ShouldNotChangeState()
    {
        var machine = new AlarmStateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        // 确认不存在的 ID
        machine.Acknowledge("nonexistent_device");

        var alarms = machine.PendingAlarms;
        var alarm = Assert.Single(alarms);
        Assert.Equal(AlarmState.UNACK, alarm.State);
    }

    /// <summary>AlarmStatesChanged 事件在报警状态变化时触发</summary>
    [Fact]
    public void AlarmStatesChanged_ShouldFireOnStateTransition()
    {
        var machine = new AlarmStateMachine();
        int eventCount = 0;
        machine.AlarmStatesChanged += () => eventCount++;

        // 触发报警 -> 事件 +1
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(payload);
        Assert.Equal(1, eventCount);

        // 确认 -> 事件 +1
        var alarm = machine.PendingAlarms[0];
        machine.Acknowledge(alarm.DeviceId);
        Assert.Equal(2, eventCount);

        // 恢复 -> 事件 +1
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_01"));
        }
        Assert.Equal(3, eventCount);
    }

    #endregion 边缘场景测试

    #region 工具方法

    private static TelemetryPayload CreateNormalPayload(string deviceId)
        => CreatePayload(deviceId, DeviceStatus.Online, ErrorCode.NoError, DataQuality.Good);

    private static TelemetryPayload CreatePayload(
        string deviceId,
        DeviceStatus status,
        ErrorCode errorCode,
        DataQuality quality = DataQuality.Good)
    {
        return new TelemetryPayload
        {
            DeviceId = deviceId,
            Temperature = Temperature.FromCelsius(25.0),
            Humidity = Humidity.FromPercent(60.0),
            StatusCode = status,
            ErrorCode = errorCode,
            QualityCode = quality
        };
    }

    #endregion 工具方法
}
