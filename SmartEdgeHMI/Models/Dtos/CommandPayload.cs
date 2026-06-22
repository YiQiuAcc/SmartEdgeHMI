using System.Text.Json.Serialization;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Models.Dtos;

public record CommandPayload(
    [property: JsonPropertyName("cmd_id")] Guid CommandId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("action")] DeviceAction Action,
    [property: JsonPropertyName("timestamp")] long TimestampUnix,
    [property: JsonPropertyName("parameters")] object? Parameters = null
);
