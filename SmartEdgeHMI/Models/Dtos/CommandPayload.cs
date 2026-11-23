using System.Text.Json.Serialization;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Models.Dtos;

/// <summary>下行命令报文, 由 JSON 协议序列化后发往设备</summary>
/// <param name="CommandId">命令唯一标识, 用于设备侧去重与上位机对账</param>
/// <param name="TimestampUnix">发出时刻的 Unix 秒, 设备侧据此丢弃过期的重放命令</param>
/// <param name="Parameters">命令参数; 具体结构由 <paramref name="Action"/> 决定, 为 null 时不下发该字段</param>
public record CommandPayload(
    [property: JsonPropertyName("cmd_id")] Guid CommandId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("action")] DeviceAction Action,
    [property: JsonPropertyName("timestamp")] long TimestampUnix,
    [property: JsonPropertyName("parameters")] object? Parameters = null
);
