namespace SmartEdgeHMI.Services;

// 物理层只处理 Byte, 没有业务逻辑
public interface ISerialPortService
{
    string[] GetAvailablePortNames();

    void OpenPort(string portName, int baudRate);

    void ClosePort(string portName);

    // 发送纯字节流 (Modbus)
    Task WriteBytesAsync(string portName, byte[] data, int length);

    // 发送字符串 (JSON)
    Task WriteStringAsync(string portName, string text);
}
