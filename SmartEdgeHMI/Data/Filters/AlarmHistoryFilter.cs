namespace SmartEdgeHMI.Data.Filters;

/// <summary>报警历史查询条件, 各字段为 null 表示该维度不过滤</summary>
/// <remarks>条件在 SQL 内以 <c>@X IS NULL OR ...</c> 短路, 因此无需在调用侧拼装查询语句</remarks>
public class AlarmHistoryFilter
{
    /// <summary>起始时间(含)</summary>
    public DateTime? From { get; init; }

    /// <summary>结束时间(含)</summary>
    public DateTime? To { get; init; }

    /// <summary>设备 ID, 大小写不敏感</summary>
    public string? DeviceId { get; init; }

    /// <summary>报警码(ErrorCode 枚举名), 大小写不敏感</summary>
    public string? AlarmCode { get; init; }

    /// <summary>覆盖配置中的默认查询条数</summary>
    public int? Limit { get; init; }
}
