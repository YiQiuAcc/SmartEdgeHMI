using System.Text.Json.Serialization;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Models.DTOs;

/// <summary>上位机下发给硬件的 JSON 控制报文</summary>
public record CommandPayload(
    [property: JsonPropertyName("cmd_id")] Guid CommandId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("action")] DeviceAction Action,
    [property: JsonPropertyName("timestamp")] long TimestampUnix,
    [property: JsonPropertyName("parameters")] object? Parameters = null
);
