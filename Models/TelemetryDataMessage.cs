using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SmartEdgeHMI.Models;

public class TelemetryDataMessage(string value) : ValueChangedMessage<string>(value)
{
}
