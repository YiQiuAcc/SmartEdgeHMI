using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Database.Entities;

public class SensorReadingRecord
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public Temperature Temperature { get; set; }
    public Humidity Humidity { get; set; }
    public DeviceStatus StatusCode { get; set; }
    public ErrorCode ErrorCode { get; set; }
    public DataQuality QualityCode { get; set; } = DataQuality.Good;
}
