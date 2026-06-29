using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;

namespace SmartEdgeHMI.Models.Messages;

public record SensorReading(string PortName, Temperature Temperature, Humidity Humidity, DeviceStatus StatusCode = DeviceStatus.Online, ErrorCode ErrorCode = ErrorCode.NoError);
