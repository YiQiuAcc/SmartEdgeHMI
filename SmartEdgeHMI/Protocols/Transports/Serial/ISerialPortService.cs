namespace SmartEdgeHMI.Protocols.Transports.Serial;

/// <summary>串口传输服务: ITransportService 的串口实现。 在通用传输接口之上添加串口特有方法(端口枚举、波特率)。</summary>
public interface ISerialPortService : ITransportService
{
    /// <summary>获取系统可用串口名称列表</summary>
    string[] GetAvailablePortNames();

    /// <summary>打开指定串口</summary>
    void OpenPort(string portName, int baudRate);

    /// <summary>关闭指定串口并清理资源</summary>
    void ClosePort(string portName);
}
