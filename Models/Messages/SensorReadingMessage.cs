using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Models.Messages;

public record SensorReadingMessage(string PortName, Temperature Temperature, Humidity Humidity, DeviceStatus StatusCode = DeviceStatus.Online, ErrorCode ErrorCode = ErrorCode.NoError);
