using System.Text.Json.Serialization;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;

namespace SmartEdgeHMI.Models.Dtos;

public class TelemetryPayload
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    public Temperature Temperature { get; init; }

    [JsonPropertyName("humidity")]
    public Humidity Humidity { get; init; }

    [JsonPropertyName("status")]
    public DeviceStatus StatusCode { get; init; }

    [JsonPropertyName("err_code")]
    public ErrorCode ErrorCode { get; init; }

    [JsonPropertyName("quality")]
    public DataQuality QualityCode { get; init; } = DataQuality.Good;
}
