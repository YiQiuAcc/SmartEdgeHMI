using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Models.Entities;

public class SensorReadingEntity
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public DeviceStatus StatusCode { get; set; }
    public ErrorCode ErrorCode { get; set; }
    public DataQuality QualityCode { get; set; } = DataQuality.Good;
}
