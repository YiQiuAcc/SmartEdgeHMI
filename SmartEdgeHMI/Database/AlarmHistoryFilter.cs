namespace SmartEdgeHMI.Database;

public class AlarmHistoryFilter
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? DeviceId { get; init; }
    public string? AlarmCode { get; init; }
    public int? Limit { get; init; }
}
