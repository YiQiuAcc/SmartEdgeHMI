using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Models.Messages;

public class TrendDataLoadedMessage
{
    public string PortName { get; }
    public List<SensorReadingEntity> Data { get; }

    public TrendDataLoadedMessage(string portName, List<SensorReadingEntity> data)
    {
        PortName = portName;
        Data = data;
    }
}
