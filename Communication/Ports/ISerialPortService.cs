namespace SmartEdgeHMI.Communication.Ports;

public interface ISerialPortService
{
    string[] GetAvailablePortNames();

    void OpenPort(string portName, int baudRate);

    void ClosePort(string portName);

    Task WriteBytesAsync(string portName, byte[] data, int length);

    Task WriteStringAsync(string portName, string text);
}
