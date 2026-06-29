using SmartEdgeHMI.Models.Logging;

namespace SmartEdgeHMI.Models.Messages;

public class LogUpdate(SystemLogModel data)
{
    public SystemLogModel LogData { get; } = data;
}
