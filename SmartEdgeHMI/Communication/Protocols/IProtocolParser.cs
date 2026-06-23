using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Communication.Protocols;

public interface IProtocolParser : IDisposable
{
    string Key { get; }

    /// <summary>接收原始字节数据（异步非阻塞版本） 上游调用方处于异步上下文中，不应同步阻塞线程池线程。</summary>
    ValueTask OnDataReceivedAsync(string portName, ReadOnlyMemory<byte> data);

    void OnDeviceStateChanged(string portName, ConnectionState state);
}
