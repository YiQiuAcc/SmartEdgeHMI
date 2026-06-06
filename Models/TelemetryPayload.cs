using System.Text.Json.Serialization;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Models.DTOs;

/// <summary>
/// 硬件发给上位机的 JSON 数据报文 (不可变记录)
/// 例如: {"dev_id": "Sensor_01", "temp": 25.5, "count": 100, "status": 1}
/// </summary>
public record TelemetryPayload(
    [property: JsonPropertyName("dev_id")] string DeviceId,
    [property: JsonPropertyName("temp")] double Temperature,
    [property: JsonPropertyName("count")] long OutputCount,
    [property: JsonPropertyName("status")] DeviceStatus StatusCode,
    [property: JsonPropertyName("err_code")] ErrorCode ErrorCode
);
