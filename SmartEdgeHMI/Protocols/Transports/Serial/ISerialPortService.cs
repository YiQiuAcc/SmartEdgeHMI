namespace SmartEdgeHMI.Protocols.Transports.Serial;

/// <summary>串口传输服务: <see cref="ITransportService"/> 的串口实现, 额外暴露端口枚举与开关</summary>
/// <remarks>
/// 打开端口只表示物理链路已建立, 真正可用以 <see cref="ITransportService.StateChanged"/> 收到 Connected 为准
/// </remarks>
public interface ISerialPortService : ITransportService
{
    /// <summary>枚举系统当前可见的串口名</summary>
    string[] GetAvailablePortNames();

    /// <summary>打开串口并启动后台读循环; 失败时通过 StateChanged 报 Error 不抛异常</summary>
    void OpenPort(string portName, int baudRate);

    /// <summary>关闭串口, 停止读循环并清理该端口的上下文</summary>
    void ClosePort(string portName);
}
