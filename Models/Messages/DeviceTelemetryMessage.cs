using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Models.Messages;

public record DeviceTelemetryMessage(string PortName, TelemetryPayload Payload);
