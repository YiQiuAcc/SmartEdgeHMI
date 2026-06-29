using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;

namespace SmartEdgeHMI.Core.Domain.MachineState;

public record DeviceStateSnapshot(
    string PortName,
    Temperature Temperature,
    Humidity Humidity,
    DeviceStatus StatusCode,
    ErrorCode ErrorCode,
    DataQuality Quality,
    DateTime Timestamp
);
