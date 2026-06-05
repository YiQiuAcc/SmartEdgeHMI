using SmartEdgeHMI.Constants;

namespace SmartEdgeHMI.Models;

/// <summary>遥测数据</summary>
public class DeviceTelemetry
{
    public required string DeviceId { get; set; }
    public DateTime Timestamp { get; set; }

    public bool IsOnline { get; set; }
    public QualityCode QualityCode { get; set; }
    public Status Status { get; set; }
    public ErrorCode ErrorCode { get; set; }

    // 生产数据
    public double Temperature { get; set; }
    public long OutputCount { get; set; }
}
