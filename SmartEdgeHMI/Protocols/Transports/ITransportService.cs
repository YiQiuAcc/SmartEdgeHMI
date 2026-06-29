using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Protocols;

/// <summary>
/// 传输层抽象: 定义通用的连接/断开/发送行为。
/// 协议解析层(Modbus/JSON)只依赖此接口, 不感知底层是串口还是 TCP/MQTT/WebSocket。
/// 命令通过 WriteBytesAsync 发送, 响应由传输层的后台读循环异步送达 OnDataReceivedAsync。
/// </summary>
public interface ITransportService
{
    /// <summary>连接状态变更事件</summary>
    event Action<string, ConnectionState>? StateChanged;

    /// <summary>异步写入原始字节</summary>
    Task WriteBytesAsync(string endpoint, byte[] data, int length);

    /// <summary>异步写入文本</summary>
    Task WriteStringAsync(string endpoint, string text);
}
