namespace SmartEdgeHMI.Models;

/// <summary>报警历史查询过滤条件（所有属性可空，不设置时不过滤）</summary>
public class AlarmHistoryFilter
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? DeviceId { get; init; }
    public string? AlarmCode { get; init; }
    public int? Limit { get; init; }
}
