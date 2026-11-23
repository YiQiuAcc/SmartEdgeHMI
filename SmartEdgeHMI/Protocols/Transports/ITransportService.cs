using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Protocols.Transports;

/// <summary>传输层抽象: 连接、断开与发送, 协议解析器只依赖此接口</summary>
/// <remarks>
/// 让解析层不感知底层是串口还是 TCP/MQTT/WebSocket。
/// 命令经 <see cref="WriteBytesAsync"/> 发出, 响应由传输层后台读循环主动传递给解析器,
/// 因此解析器不持有读路径, 也不应在此接口上做同步等待。
/// </remarks>
public interface ITransportService
{
    /// <summary>端口连接状态变更, 在传输层线程上触发</summary>
    event Action<string, ConnectionState>? StateChanged;

    /// <summary>写入原始字节</summary>
    /// <param name="data">待写缓冲区, 方法返回后可被调用方复用或归还</param>
    /// <param name="length">有效字节数, 可能小于 <paramref name="data"/> 长度</param>
    Task WriteBytesAsync(string endpoint, byte[] data, int length);

    /// <summary>写入文本, 实现负责编码与行结束符</summary>
    Task WriteStringAsync(string endpoint, string text);
}
