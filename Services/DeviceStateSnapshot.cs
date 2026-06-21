using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Services;

/// <summary>某个端口在某一时刻的设备遥测快照（不可变记录）</summary>
public record DeviceStateSnapshot(
    string PortName,
    Temperature Temperature,
    Humidity Humidity,
    DeviceStatus StatusCode,
    ErrorCode ErrorCode,
    DataQuality Quality,
    DateTime Timestamp
);
