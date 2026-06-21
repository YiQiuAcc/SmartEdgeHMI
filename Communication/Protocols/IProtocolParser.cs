using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Communication.Protocols;

public interface IProtocolParser : IDisposable
{
    string Key { get; }

    void OnDataReceived(string portName, ReadOnlySpan<byte> data);

    void OnDeviceStateChanged(string portName, ConnectionState state);
}
