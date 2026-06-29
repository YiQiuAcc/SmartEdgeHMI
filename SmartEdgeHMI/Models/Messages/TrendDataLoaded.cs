using SmartEdgeHMI.Database.Entities;

namespace SmartEdgeHMI.Models.Messages;

public class TrendDataLoaded(string portName, List<SensorReadingRecord> data)
{
    public string PortName { get; } = portName;
    public List<SensorReadingRecord> Data { get; } = data;
}
