namespace SmartEdgeHMI.Communication.Ports;

/// <summary>串口物理层服务: 原始字节流的收发, 不关心数据格式</summary>
public interface ISerialPortService
{
    /// <summary>获取系统可用串口名称列表</summary>
    string[] GetAvailablePortNames();

    /// <summary>打开指定串口</summary>
    /// <param name="portName">串口名(如 COM1)</param>
    /// <param name="baudRate">波特率</param>
    void OpenPort(string portName, int baudRate);

    /// <summary>关闭指定串口并清理资源</summary>
    void ClosePort(string portName);

    /// <summary>异步向串口写入指定长度的字节数据</summary>
    Task WriteBytesAsync(string portName, byte[] data, int length);

    /// <summary>异步向串口写入文本(UTF-8 编码, 自动追加 \n)</summary>
    Task WriteStringAsync(string portName, string text);
}
