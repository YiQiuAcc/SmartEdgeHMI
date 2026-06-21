using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

/// <summary>协议解析器抽象：消费物理层原始字节流，产生强类型遥测消息</summary>
public interface IProtocolParser : IDisposable
{
    /// <summary>Keyed DI 的键（"JSON" / "Modbus"）</summary>
    string Key { get; }

    /// <summary>接收到原始字节数据时由协凋器调用</summary>
    void OnDataReceived(string portName, ReadOnlySpan<byte> data);

    /// <summary>端口连接状态变化时由协凋器调用</summary>
    void OnDeviceStateChanged(string portName, ConnectionState state);
}
