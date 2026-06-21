using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Models.Messages;

public record DeviceTelemetry(string PortName, TelemetryPayload Payload);
