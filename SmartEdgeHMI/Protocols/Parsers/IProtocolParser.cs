using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Protocols.Parsers;

public interface IProtocolParser : IDisposable
{
    /// <summary>协议标识键, 用于 DI 按名称解析(如 "Modbus"、"JSON")</summary>
    string Key { get; }

    /// <summary>接收原始字节数据(异步非阻塞版本) 上游调用方处于异步上下文中, 不应同步阻塞线程池线程。</summary>
    ValueTask OnDataReceivedAsync(string portName, ReadOnlyMemory<byte> data);

    /// <summary>设备物理状态变更通知: 处理连接/断连时的资源清理和轮询启停</summary>
    void OnDeviceStateChanged(string portName, ConnectionState state);
}
