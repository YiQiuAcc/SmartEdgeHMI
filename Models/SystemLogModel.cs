namespace SmartEdgeHMI.Models;

// 日志的数据模型 (UI 绑定实体)
public class SystemLogModel
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
