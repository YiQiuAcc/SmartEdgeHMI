namespace SmartEdgeHMI.Models.Enums;

/// <summary>设备整体的宏观运行状态 (Device State) 职责：决定 UI 卡片的颜色（绿、灰、黄、红）</summary>
public enum DeviceStatus : byte
{
    Offline = 0,      // 离线/失联 (灰色)
    Online = 1,       // 正常在线 (绿色)
    Stopped = 2,      // 正常停机/待机 (蓝色)
    Maintenance = 3,  // 维护/配置模式 (黄色)
    Fault = 4         // 故障锁定 (红色)
}

/// <summary>遥测数据的质量码 (Data Quality) 职责：参照 OPC-UA 标准，仅用于标识“当前这批数据点是否可信”</summary>
public enum DataQuality : byte
{
    Good = 0,         // 数据完全可信
    Uncertain = 1,    // 数据可能不准（如传感器处于预热期、正在校准）
    Bad = 2           // 数据无效（如传感器断线，传回的温度是默认的 -999）
}

/// <summary>
/// 具体的故障/错误原因 (Error Code) 职责：仅在 DeviceStatus == Fault 或 DataQuality == Bad 时，提供具体原因
/// </summary>
public enum ErrorCode : ushort
{
    NoError = 0,

    // 1xx: 通信链路层错误
    Timeout = 101,
    ChecksumFailed = 102,
    Unauthorized = 103,

    // 2xx: 硬件与传感器错误
    SensorDisconnected = 201,
    PowerLow = 202,

    // 3xx: 业务逻辑错误
    NotConfigured = 301,
    ThresholdExceeded = 302
}

/// <summary>HMI 下发给边缘网关的主动控制动作 (Device Action)</summary>
public enum DeviceAction : byte
{
    None = 0,
    Start = 1,
    Stop = 2,
    Reset = 3,        // 紧急复位
    Configure = 4,    // 下发配置参数 (如更改报警阈值)
    TriggerSample = 5 // 手动触发单次采样
}

/// <summary>上位机与下位机之间的通信协议类型</summary>
public enum CommunicationProtocol : byte
{
    JSON = 0,
    Modbus = 1
}

public enum ModbusFunctionCode
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
