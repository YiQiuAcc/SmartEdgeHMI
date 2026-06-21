using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

/// <summary>某个端口在某一时刻的设备遥测快照（不可变记录）</summary>
public record DeviceStateSnapshot(
    string PortName,
    double Temperature,
    double Humidity,
    DeviceStatus StatusCode,
    ErrorCode ErrorCode,
    DataQuality Quality,
    DateTime Timestamp
);
