using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Tests.State;

/// <summary>ISA-18.2 报警状态机: 四态迁移、恢复去抖、一键确认与事件窗口内的可见性</summary>
/// <remarks>
/// 所有用例共用一个真实 <see cref="SettingsService"/>, 但设置目录指向临时目录, 不触碰用户配置;
/// 防抖次数取配置默认值 3, 因此每个"恢复"场景都需要连续 3 条正常读数。
/// </remarks>
public class AlarmStateMachineTests : IDisposable
{
    // 每个测试方法创建的临时设置目录, Dispose 时统一清理
    private readonly List<string> _tempDirectories = [];

    private AlarmStateMachine CreateMachine()
    {
        // 使用临时目录重载, 避免测试写真实的 %LOCALAPPDATA%\SmartEdgeHMI\settings.json
        string settingsDirectory = Path.Combine(
            Path.GetTempPath(), "SmartEdgeHMI.Tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(settingsDirectory);

        var config = new ConfigurationBuilder().Build();
        var settingsService = new SettingsService(config, settingsDirectory);
        return new AlarmStateMachine(settingsService);
    }

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // 清理失败不影响测试结果
            }
        }
    }

    #region 基础功能测试

    /// <summary>正常遥测数据不应触发报警</summary>
    [Fact]
    public void Evaluate_NormalPayload_ShouldReturnNull()
    {
        var machine = CreateMachine();
        var payload = CreateNormalPayload("Sensor_01");

        var result = machine.Evaluate(payload);

        Assert.Null(result);
    }

    /// <summary>Bad Quality 遥测数据不应触发报警</summary>
    [Fact]
    public void Evaluate_BadQuality_ShouldReturnNull()
    {
        var machine = CreateMachine();
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.NoError, DataQuality.Bad);

        var result = machine.Evaluate(payload);

        Assert.Null(result);
    }

    /// <summary>首次收到错误码应触发报警, 返回 UNACK 状态的 AlarmRecord</summary>
    [Fact]
    public void Evaluate_FirstError_ShouldTriggerAlarm_UNACK()
    {
        var machine = CreateMachine();
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);

        var result = machine.Evaluate(payload);

        Assert.NotNull(result);
        Assert.Equal("Sensor_01", result.DeviceId);
        Assert.Equal(nameof(ErrorCode.ThresholdExceeded), result.AlarmCode);
        Assert.Equal(AlarmState.UNACK, result.State);
        Assert.Null(result.AcknowledgedAt);
        Assert.Null(result.ClearedAt);
    }

    /// <summary>相同错误连续上报不应重复创建报警记录</summary>
    [Fact]
    public void Evaluate_SameErrorRepeated_ShouldNotCreateNewAlarm()
    {
        var machine = CreateMachine();
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
        var machine = CreateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload); // 触发报警

        // 发送多次正常数据, 达到防抖阈值 (AlarmRecoveryDebounceCount = 3)
        var normalPayload = CreateNormalPayload("Sensor_01");
        AlarmRecord? result = null;
        for (int i = 0; i < 3; i++)
        {
            result = machine.Evaluate(normalPayload);
        }

        // 最后一次 Evaluate 应驱动状态变迁, 但返回 null(不是新报警)
        Assert.Null(result);

        var pendingAlarms = machine.PendingAlarms;
        var alarm = Assert.Single(pendingAlarms);
        Assert.Equal(AlarmState.RTN_UNACK, alarm.State);
        Assert.NotNull(alarm.ClearedAt);
    }

    #endregion 基础功能测试

    #region ISA-18.2 状态机转移测试

    /// <summary>UNACK → ACK: 操作员确认报警后应进入 ACK 状态</summary>
    [Fact]
    public void Acknowledge_UnacknowledgedAlarm_ShouldTransitionToAck()
    {
        var machine = CreateMachine();
        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        var alarm = machine.Evaluate(payload);
        Assert.NotNull(alarm);

        machine.Acknowledge(alarm.DeviceId);

        var pendingAlarms = machine.PendingAlarms;
        var acked = Assert.Single(pendingAlarms);
        Assert.Equal(AlarmState.ACK, acked.State);
        Assert.NotNull(acked.AcknowledgedAt);
    }

    /// <summary>RTN_UNACK → NORMAL: 恢复未确认状态下确认应转为 NORMAL(报警消除)</summary>
    [Fact]
    public void Acknowledge_RTN_UNACK_ShouldTransitionToNormal()
    {
        var machine = CreateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        var pendingBeforeAck = machine.PendingAlarms;
        var rtnUnackAlarm = Assert.Single(pendingBeforeAck);
        Assert.Equal(AlarmState.RTN_UNACK, rtnUnackAlarm.State);

        machine.Acknowledge(rtnUnackAlarm.DeviceId);

        Assert.Empty(machine.PendingAlarms);
    }

    /// <summary>ACK → NORMAL: 已确认后恢复应转为 NORMAL</summary>
    [Fact]
    public void Acknowledge_ThenRecover_ShouldTransitionToNormal()
    {
        var machine = CreateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        var alarm = machine.Evaluate(errorPayload);
        Assert.NotNull(alarm);

        machine.Acknowledge(alarm.DeviceId);

        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        Assert.Empty(machine.PendingAlarms);
    }

    /// <summary>RTN_UNACK → UNACK: 恢复未确认状态下重新报警应回到 UNACK</summary>
    [Fact]
    public void Reactivate_RTN_UNACK_ShouldTransitionBackToUNACK()
    {
        var machine = CreateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        Assert.Single(machine.PendingAlarms);
        Assert.Equal(AlarmState.RTN_UNACK, machine.PendingAlarms[0].State);

        machine.Evaluate(errorPayload);

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
        var machine = CreateMachine();

        var alarm1 = machine.Evaluate(CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded));
        var alarm2 = machine.Evaluate(CreatePayload("Sensor_02", DeviceStatus.Online, ErrorCode.SensorDisconnected));
        var alarm3 = machine.Evaluate(CreatePayload("Sensor_03", DeviceStatus.Online, ErrorCode.PowerLow));

        Assert.NotNull(alarm1);
        Assert.NotNull(alarm2);
        Assert.NotNull(alarm3);

        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_03"));
        }

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
        var machine = CreateMachine();

        var ex = Record.Exception(() => machine.AcknowledgeAll());
        Assert.Null(ex);
    }

    #endregion 一键确认测试

    #region 边缘场景测试

    /// <summary>防抖计数功能验证: 恢复信号需要连续 N 次才能触发状态变更</summary>
    [Fact]
    public void RecoveryDebounce_ShouldRequireMultipleNormalReadings()
    {
        var machine = CreateMachine();
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
        var machine = CreateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        var alarm1 = machine.Evaluate(errorPayload);
        Assert.NotNull(alarm1);

        machine.Acknowledge(alarm1.DeviceId);
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_01"));
        }

        Assert.Empty(machine.PendingAlarms);

        var alarm2 = machine.Evaluate(errorPayload);
        Assert.NotNull(alarm2);
        Assert.Equal(AlarmState.UNACK, alarm2.State);
        Assert.NotSame(alarm1, alarm2);
    }

    /// <summary>ActiveAlarms 只返回非 NORMAL 状态的报警</summary>
    [Fact]
    public void ActiveAlarms_ShouldNotIncludeNormalState()
    {
        var machine = CreateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        Assert.Single(machine.ActiveAlarms);
        Assert.True(machine.ActiveAlarms.ContainsKey("Sensor_01"));

        var alarm = machine.PendingAlarms[0];
        machine.Acknowledge(alarm.DeviceId);
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_01"));
        }

        Assert.Empty(machine.ActiveAlarms);
    }

    /// <summary>多个不同设备的报警应独立管理</summary>
    [Fact]
    public void MultipleDevices_AlarmsShouldBeIndependent()
    {
        var machine = CreateMachine();

        machine.Evaluate(CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded));
        machine.Evaluate(CreatePayload("Sensor_02", DeviceStatus.Online, ErrorCode.SensorDisconnected));

        Assert.Equal(2, machine.PendingAlarms.Count);

        var alarm1 = machine.PendingAlarms.First(a => a.DeviceId == "Sensor_01");
        machine.Acknowledge(alarm1.DeviceId);

        Assert.Equal(AlarmState.ACK, machine.PendingAlarms.First(a => a.DeviceId == "Sensor_01").State);
        Assert.Equal(AlarmState.UNACK, machine.PendingAlarms.First(a => a.DeviceId == "Sensor_02").State);
    }

    /// <summary>确认不存在的 AlarmId 不应影响状态</summary>
    [Fact]
    public void Acknowledge_NonExistentAlarmId_ShouldNotChangeState()
    {
        var machine = CreateMachine();
        var errorPayload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(errorPayload);

        machine.Acknowledge("nonexistent_device");

        var alarms = machine.PendingAlarms;
        var alarm = Assert.Single(alarms);
        Assert.Equal(AlarmState.UNACK, alarm.State);
    }

    /// <summary>AlarmStatesChanged 事件在报警状态变化时触发</summary>
    [Fact]
    public void AlarmStatesChanged_ShouldFireOnStateTransition()
    {
        var machine = CreateMachine();
        int eventCount = 0;
        machine.AlarmStatesChanged += () => eventCount++;

        var payload = CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded);
        machine.Evaluate(payload);
        Assert.Equal(1, eventCount);

        var alarm = machine.PendingAlarms[0];
        machine.Acknowledge(alarm.DeviceId);
        Assert.Equal(2, eventCount);

        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreateNormalPayload("Sensor_01"));
        }
        Assert.Equal(3, eventCount);
    }

    #endregion 边缘场景测试

    #region 报警恢复态落库回归测试

    /// <summary>ACK 恢复转 NORMAL 时, 事件处理期间 PendingAlarms 仍应取到该记录(否则数据库永远停在 ACK), 事件返回后再移出活动表</summary>
    [Fact]
    public void 恢复时_事件触发期间NORMAL记录仍可见_事件后从活动列表移除()
    {
        var machine = CreateMachine();
        List<AlarmRecord>? snapshotDuringEvent = null;
        int eventCount = 0;
        machine.AlarmStatesChanged += () =>
        {
            eventCount++;
            if (eventCount == 3) // 第 3 次: 第 3 条正常遥测触发的恢复事件
            {
                snapshotDuringEvent = [.. machine.PendingAlarms];
            }
        };

        var alarm = machine.Evaluate(CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded));
        Assert.NotNull(alarm);
        machine.Acknowledge(alarm.DeviceId);

        // 连续 3 条正常数据, 达到防抖阈值 (RecoveryDebounceCount = 3), 第 3 条触发 ACK → NORMAL
        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        // 事件处理期间: NORMAL 记录(带 ClearedAt)必须仍能被 PendingAlarms 取到
        Assert.NotNull(snapshotDuringEvent);
        var normal = Assert.Single(snapshotDuringEvent);
        Assert.Equal(AlarmState.NORMAL, normal.State);
        Assert.NotNull(normal.ClearedAt);

        // 事件返回后: 记录已从活动列表移除
        Assert.Empty(machine.PendingAlarms);
        Assert.Empty(machine.ActiveAlarms);
    }

    /// <summary>一键确认把 RTN_UNACK 转成 NORMAL 时, 事件处理期间 PendingAlarms 同样应取到该记录</summary>
    [Fact]
    public void 一键确认_RTN_UNACK转NORMAL_事件期间同样可见()
    {
        var machine = CreateMachine();
        List<AlarmRecord>? snapshotDuringEvent = null;
        int eventCount = 0;
        machine.AlarmStatesChanged += () =>
        {
            eventCount++;
            if (eventCount == 3) // 第 3 次: 一键确认触发的事件
            {
                snapshotDuringEvent = [.. machine.PendingAlarms];
            }
        };

        machine.Evaluate(CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded));

        // 连续 3 条正常数据: UNACK → RTN_UNACK
        var normalPayload = CreateNormalPayload("Sensor_01");
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(normalPayload);
        }

        machine.AcknowledgeAll();

        // 事件处理期间: NORMAL 记录必须仍能被 PendingAlarms 取到
        Assert.NotNull(snapshotDuringEvent);
        var normal = Assert.Single(snapshotDuringEvent);
        Assert.Equal(AlarmState.NORMAL, normal.State);
        Assert.NotNull(normal.ClearedAt);

        // 事件返回后: 记录已从活动列表移除
        Assert.Empty(machine.PendingAlarms);
        Assert.Empty(machine.ActiveAlarms);
    }

    /// <summary>一键确认对已确认(ACK)的记录无副作用: 不产生新的 AcknowledgedAt</summary>
    [Fact]
    public void 一键确认_已确认记录_不产生新的确认时间()
    {
        var machine = CreateMachine();
        var alarm = machine.Evaluate(CreatePayload("Sensor_01", DeviceStatus.Online, ErrorCode.ThresholdExceeded));
        Assert.NotNull(alarm);

        machine.Acknowledge(alarm.DeviceId);
        var ackedAtBefore = machine.PendingAlarms[0].AcknowledgedAt;
        Assert.NotNull(ackedAtBefore);

        System.Threading.Thread.Sleep(20); // 留出时间差, 若被重复确认则 AcknowledgedAt 必然变化
        machine.AcknowledgeAll();

        var pending = machine.PendingAlarms;
        var acked = Assert.Single(pending);
        Assert.Equal(AlarmState.ACK, acked.State);
        Assert.Equal(ackedAtBefore, acked.AcknowledgedAt);
    }

    #endregion 报警恢复态落库回归测试

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
}
