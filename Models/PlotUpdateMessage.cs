namespace SmartEdgeHMI.Models;

public class PlotUpdateMessage(string portName, double temp)
{
    public string PortName { get; } = portName;
    public double Temperature { get; } = temp;
}
