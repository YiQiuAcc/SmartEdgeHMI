using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Data.Entities;

public class AlarmRecordEntity
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string AlarmCode { get; set; } = string.Empty;
    public double TriggerValue { get; set; }
    public DataQuality QualityCode { get; set; } = DataQuality.Good;
}
