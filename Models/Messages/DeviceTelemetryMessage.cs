using SmartEdgeHMI.Models.DTOs;

namespace SmartEdgeHMI.Models.Messages;

public record DeviceTelemetryMessage(string PortName, TelemetryPayload Payload);
