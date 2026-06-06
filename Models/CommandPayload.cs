using System.Text.Json.Serialization;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Models.DTOs;

/// <summary>上位机下发给硬件的 JSON 控制报文</summary>
public record CommandPayload(
    [property: JsonPropertyName("cmd_id")] Guid CommandId,
    [property: JsonPropertyName("dev_id")] string DeviceId,
    [property: JsonPropertyName("action")] DeviceAction Action,
    [property: JsonPropertyName("timestamp")] long TimestampUnix, // 下发 Unix 时间戳, 单片机更好解析
    [property: JsonPropertyName("parameters")] object? Parameters = null // 替代原来的 JsonElement, 更易于赋值
);
