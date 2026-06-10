namespace SmartEdgeHMI.Models.Messages;

// 用于广播的消息记录
public class LogUpdateMessage(SystemLogModel data)
{
    public SystemLogModel LogData { get; } = data;
}
