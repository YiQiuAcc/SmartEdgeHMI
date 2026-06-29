using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.MachineState;

public record DeviceStateSnapshot(
    string PortName,
    Temperature Temperature,
    Humidity Humidity,
    DeviceStatus StatusCode,
    ErrorCode ErrorCode,
    DataQuality Quality,
    DateTime Timestamp
);
