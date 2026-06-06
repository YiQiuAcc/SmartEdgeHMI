using SmartEdgeHMI.Models.DTOs;

namespace SmartEdgeHMI.Services;

public interface ISerialPortService
{
    public string[] GetAvailablePortNames();

    public void OpenPort(string portName, int baudRate);

    public void ClosePort(string portName);

    public Task SendCommandAsync(string portName, CommandPayload commandPayload);
}
