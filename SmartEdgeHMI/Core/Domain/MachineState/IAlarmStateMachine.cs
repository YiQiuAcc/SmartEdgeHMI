using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Core.Domain.MachineState;

/// <summary>ISA-18.2 报警状态机: 管理 UNACK / ACK / RTN_UNACK / NORMAL 四态迁移与恢复去抖</summary>
/// <remarks>
/// 实现须保证所有成员线程安全, 可从协议解析线程直接调用。
/// <see cref="AlarmStatesChanged"/> 在内部锁之外触发, 处理器内不得反向调用本接口的加锁成员。
/// </remarks>
public interface IAlarmStateMachine
{
    /// <summary>评估一次遥测数据</summary>
    /// <returns>仅在新报警触发(UNACK 建立)时返回新建记录; 已有报警的状态迁移返回 null, 由事件通知</returns>
    AlarmRecord? Evaluate(TelemetryPayload payload);

    /// <summary>当前活跃报警快照(设备ID, 错误码), 为独立副本, 调用方修改不影响内部状态</summary>
    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }

    /// <summary>待处理报警(含已恢复但尚未同步写入的 NORMAL 记录)</summary>
    IReadOnlyList<AlarmRecord> PendingAlarms { get; }

    void Acknowledge(string deviceId);

    void AcknowledgeAll();

    /// <summary>报警状态发生任意迁移后触发, 用于 UI 刷新与数据库同步</summary>
    event Action? AlarmStatesChanged;
}
