using System.Text.Json.Serialization;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;

namespace SmartEdgeHMI.Models.Dtos;

/// <summary>一次遥测上报的领域载荷, 在解析器、告警状态机与视图模型之间传递</summary>
/// <remarks>
/// 与设备 JSON 报文分离: 报文里的字段名(deviceId/temperature/err_code)由 <see cref="JsonPropertyNameAttribute"/> 固定,
/// 设备固件改字段名时必须同步修改这里, 解析器本身不感知报文层命名。
/// </remarks>
public class TelemetryPayload
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>温度, 单位 °C; JSON 中为裸数值</summary>
    [JsonPropertyName("temperature")]
    public Temperature Temperature { get; init; }

    /// <summary>相对湿度, 单位 %RH; JSON 中为裸数值</summary>
    [JsonPropertyName("humidity")]
    public Humidity Humidity { get; init; }

    [JsonPropertyName("status")]
    public DeviceStatus StatusCode { get; init; }

    /// <summary>设备错误码; 非 NoError 时进入报警评估</summary>
    [JsonPropertyName("err_code")]
    public ErrorCode ErrorCode { get; init; }

    /// <summary>数据质量标记; Bad 的载荷不参与报警判定</summary>
    [JsonPropertyName("quality")]
    public DataQuality QualityCode { get; init; } = DataQuality.Good;
}
