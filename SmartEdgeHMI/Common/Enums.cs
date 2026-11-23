namespace SmartEdgeHMI.Common;

/// <summary>设备运行状态, 数值直接进入 Modbus 保持寄存器 2 与 JSON 的 status 字段</summary>
/// <remarks>与固件中的 state_status 取值一一对应, 增删成员须同步固件</remarks>
public enum DeviceStatus : byte
{
    Offline = 0,
    Online = 1,
    Stopped = 2,
    Maintenance = 3,
    Fault = 4
}

/// <summary>数据质量标记; Bad 的遥测不参与报警评估, 但仍写数据库留痕</summary>
public enum DataQuality : byte
{
    Good = 0,
    Uncertain = 1,
    Bad = 2
}

/// <summary>设备与链路错误码, 非 NoError 即视为故障并驱动 ISA-18.2 报警</summary>
/// <remarks>
/// 数值按类别分段: 1xx 通信与协议, 2xx 硬件与供电, 3xx 配置与阈值。
/// 以枚举名写入 AlarmHistory.AlarmCode, 重命名会导致历史查询按名匹配失效。
/// </remarks>
public enum ErrorCode : ushort
{
    NoError = 0,
    Timeout = 101,
    ChecksumFailed = 102,
    Unauthorized = 103,
    SensorDisconnected = 201,
    PowerLow = 202,
    NotConfigured = 301,
    ThresholdExceeded = 302
}

/// <summary>下行控制动作, 作为 JSON 命令的 action 字段</summary>
public enum DeviceAction : byte
{
    None = 0,
    Start = 1,
    Stop = 2,
    Reset = 3,
    Configure = 4,
    TriggerSample = 5
}

/// <summary>当前生效的解析协议; 一处切换即决定串口数据交给哪个解析器</summary>
public enum CommunicationProtocol : byte
{
    JSON = 0,
    Modbus = 1
}

/// <summary>ISA-18.2 报警状态</summary>
/// <remarks>以 INTEGER 持久化到 AlarmHistory.State, 成员不得重新编号</remarks>
public enum AlarmState : byte
{
    /// <summary>已触发, 操作员未确认</summary>
    UNACK = 0,

    /// <summary>操作员已确认, 故障仍在</summary>
    ACK = 1,

    /// <summary>故障已消失但未确认</summary>
    RTN_UNACK = 2,

    /// <summary>已确认且已恢复, 即将移出活动表</summary>
    NORMAL = 3
}

/// <summary>Modbus 功能码, 仅实现 FC03 轮询与 FC06 单寄存器写入</summary>
public enum ModbusFunctionCode : byte
{
    ReadCoils = 1,
    ReadDiscreteInputs = 2,
    ReadHoldingRegisters = 3,
    ReadInputRegisters = 4,
    WriteSingleCoil = 5,
    WriteSingleRegister = 6,
    WriteMultipleCoils = 15,
    WriteMultipleRegisters = 16
}
