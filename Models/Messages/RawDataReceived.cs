namespace SmartEdgeHMI.Models.Messages;

/// <summary>物理层收包事件：承载串口原始字节流, 供协议层消费</summary>
public class RawDataReceived(string portName, byte[] data)
{
    public string PortName { get; } = portName;
    public byte[] Data { get; } = data;
}
