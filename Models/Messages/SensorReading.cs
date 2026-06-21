using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Models.Messages;

public record SensorReading(string PortName, Temperature Temperature, Humidity Humidity, DeviceStatus StatusCode = DeviceStatus.Online, ErrorCode ErrorCode = ErrorCode.NoError);
