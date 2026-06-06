using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Models;

namespace SmartEdgeHMI.Services;

public class SerialPortService : ISerialPortService
{
    private readonly ConcurrentDictionary<string, (SerialPort Port, CancellationTokenSource Cts)> _activePorts = new();

    public string[] GetAvailablePortNames()
    {
        return SerialPort.GetPortNames();
    }

    public void OpenPort(string portName, int baudRate = 115200)
    {
        if (_activePorts.ContainsKey(portName)) return;

        SerialPort serialPort = new(portName, baudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };

        var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        serialPort.Open();

        if (_activePorts.TryAdd(portName, (serialPort, cts)))
        {
            Task.Factory.StartNew(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var portMessage = serialPort.ReadLine();
                            if (!string.IsNullOrWhiteSpace(portMessage))
                            {
                                WeakReferenceMessenger.Default.Send(new TelemetryDataMessage(portName, portMessage));
                            }
                        }
                        catch (TimeoutException) { }
                    }
                }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected error in listening task for port {PortName}", portName);
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public void ClosePort(string portName)
    {
        if (_activePorts.TryRemove(portName, out var portData))
        {
            portData.Cts.Cancel(); // 发出取消信号
            try
            {
                portData.Port.Close();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while closing port {PortName}", portName);
            }
            finally
            {
                portData.Port.Dispose();
                portData.Cts.Dispose();
            }
        }
    }

    public void SendCommandAsync(string portName, DeviceCommand deviceCommand)
    {
        if (_activePorts.TryGetValue(portName, out var portData))
        {
            try
            {
                if (portData.Port.IsOpen)
                {
                    string json = JsonSerializer.Serialize(deviceCommand);
                    portData.Port.WriteLine(json);
                    Log.Information("Sent command to {PortName}: {Command}", portName, json);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send command to {PortName}", portName);
            }
        }
    }
}
