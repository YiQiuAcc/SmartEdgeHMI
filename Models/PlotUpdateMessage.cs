namespace SmartEdgeHMI.Models;

public class PlotUpdateMessage(double temp)
{
    public double Temperature { get; } = temp;
}
