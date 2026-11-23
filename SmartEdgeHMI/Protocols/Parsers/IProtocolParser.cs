using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Protocols.Parsers;

/// <summary>协议解析器: 把串口原始字节流还原为领域消息, 并负责按协议节奏发起轮询</summary>
/// <remarks>
/// 生命周期与 DI 单例一致, 一个实例可服务多个端口, 因此所有状态必须按 <c>portName</c> 分区。
/// <see cref="OnDataReceivedAsync"/> 由串口读循环串行调用(单端口), 不同端口之间可能并发。
/// </remarks>
public interface IProtocolParser : IDisposable
{
    /// <summary>协议标识键, 用于 DI 按名称解析(如 "Modbus"、"JSON")</summary>
    string Key { get; }

    /// <summary>接收一段原始字节</summary>
    /// <remarks>
    /// 实现只负责入队/解析, 不得同步阻塞读循环; 缓冲区所有权归调用方,
    /// 需要跨调用保留数据时必须自行复制。
    /// </remarks>
    ValueTask OnDataReceivedAsync(string portName, ReadOnlyMemory<byte> data);

    /// <summary>端口连接状态变更: 启停轮询、清理该端口的解析状态</summary>
    void OnDeviceStateChanged(string portName, ConnectionState state);
}
