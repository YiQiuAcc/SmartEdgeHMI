using SmartEdgeHMI.Models;

namespace SmartEdgeHMI.Services;

public interface ISerialPortService
{
    public string[] GetAvailablePortNames();

    public void OpenPort(string portName, int baudRate);

    public void ClosePort(string portName);

    public void SendCommandAsync(string portName, DeviceCommand deviceCommand);
}
