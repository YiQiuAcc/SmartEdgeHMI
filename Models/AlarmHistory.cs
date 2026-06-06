namespace SmartEdgeHMI.Models;

public class AlarmHistory
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string AlarmCode { get; set; } = string.Empty;
}
