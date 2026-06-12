using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Models.Messages;

public record SensorReadingMessage(string PortName, float Temperature, float Humidity, DeviceStatus StatusCode = DeviceStatus.Online, ErrorCode ErrorCode = ErrorCode.NoError);
