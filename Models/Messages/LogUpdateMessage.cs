using SmartEdgeHMI.Infrastructure.Logging;

namespace SmartEdgeHMI.Models.Messages;

public class LogUpdateMessage(SystemLogModel data)
{
    public SystemLogModel LogData { get; } = data;
}
