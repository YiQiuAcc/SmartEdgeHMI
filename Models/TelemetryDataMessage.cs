using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SmartEdgeHMI.Models;

public class TelemetryDataMessage(string portName, string value) : ValueChangedMessage<string>(value)
{
    public string PortName { get; } = portName;
}
