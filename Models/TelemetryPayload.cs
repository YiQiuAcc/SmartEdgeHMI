using System.Text.Json.Serialization;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Models.DTOs;

/// <summary>
/// 硬件发给上位机的 JSON 数据报文
/// 例如: {"dev_id":"Sensor_01","temp":25.5,"count":100,"status":1,"err_code":0,"quality":0}
/// </summary>
public class TelemetryPayload
{
    [JsonPropertyName("dev_id")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("temp")]
    public double Temperature { get; init; }

    [JsonPropertyName("count")]
    public long OutputCount { get; init; }

    [JsonPropertyName("status")]
    public DeviceStatus StatusCode { get; init; }

    [JsonPropertyName("err_code")]
    public ErrorCode ErrorCode { get; init; }

    [JsonPropertyName("quality")]
    public DataQuality QualityCode { get; init; } = DataQuality.Good;
}
