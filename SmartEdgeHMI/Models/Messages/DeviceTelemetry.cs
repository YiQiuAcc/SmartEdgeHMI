using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Models.Messages;

/// <summary>解析完成的一帧遥测, 由协议解析器在串口读线程上广播</summary>
/// <param name="PortName">数据来源端口, 多路设备并行接入时用于区分通道</param>
public record DeviceTelemetry(string PortName, TelemetryPayload Payload);
