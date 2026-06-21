using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Models.Messages;

public class TrendDataLoadedMessage(string portName, List<SensorReadingEntity> data)
{
    public string PortName { get; } = portName;
    public List<SensorReadingEntity> Data { get; } = data;
}
