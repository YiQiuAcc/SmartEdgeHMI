using System.Text.Json.Serialization;
using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Models.DTOs;

/// <summary>
/// 硬件发给上位机的 JSON 数据报文
/// 例如: {"deviceId":"Sensor_01","temperature":25.5,"humidity":45.0,"status":1}
/// </summary>
public class TelemetryPayload
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    public Temperature Temperature { get; init; }

    [JsonPropertyName("humidity")]
    public Humidity Humidity { get; init; }

    [JsonPropertyName("count")]
    public long OutputCount { get; init; }

    [JsonPropertyName("status")]
    public DeviceStatus StatusCode { get; init; }

    [JsonPropertyName("err_code")]
    public ErrorCode ErrorCode { get; init; }

    [JsonPropertyName("quality")]
    public DataQuality QualityCode { get; init; } = DataQuality.Good;
}
