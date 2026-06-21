using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Communication.Ports;
using SmartEdgeHMI.Communication.Protocols;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Communication;

public class DeviceCommunicationCoordinator : IDeviceCommunicationCoordinator,
    IRecipient<RawDataReceivedMessage>,
    IRecipient<DeviceStateChangedMessage>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISerialPortService _serialPortService;
    private readonly ConnectionViewModel _connectionVm;

    public DeviceCommunicationCoordinator(
        IServiceProvider serviceProvider,
        ISerialPortService serialPortService,
        ConnectionViewModel connectionVm)
    {
        _serviceProvider = serviceProvider;
        _serialPortService = serialPortService;
        _connectionVm = connectionVm;
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public void Receive(RawDataReceivedMessage message)
    {
        if (message.Data.Length == 0) return;

        string? key = _connectionVm.SelectedProtocol switch
        {
            CommunicationProtocol.JSON => "JSON",
            CommunicationProtocol.Modbus => "Modbus",
            _ => null
        };

        if (key is null) return;

        var parser = _serviceProvider.GetRequiredKeyedService<IProtocolParser>(key);
        parser.OnDataReceived(message.PortName, message.Data.AsSpan());
    }

    public void Receive(DeviceStateChangedMessage message)
    {
        NotifyParser("JSON", message);
        NotifyParser("Modbus", message);
    }

    private void NotifyParser(string key, DeviceStateChangedMessage message)
    {
        try
        {
            var parser = _serviceProvider.GetKeyedService<IProtocolParser>(key);
            parser?.OnDeviceStateChanged(message.PortName, message.State);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Coordinator] 通知 {Key} 解析器状态变更失败", key);
        }
    }

    public async Task SendThresholdAsync(double value)
    {
        var protocol = _connectionVm.SelectedProtocol;
        foreach (string portName in _connectionVm.ConnectedPorts.ToList())
        {
            switch (protocol)
            {
                case CommunicationProtocol.JSON:
                {
                    var command = new CommandPayload(
                        CommandId: Guid.NewGuid(),
                        DeviceId: AppConstants.DefaultDeviceName,
                        Action: DeviceAction.Configure,
                        TimestampUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Parameters: value
                    );
                    await _serialPortService.WriteStringAsync(portName, JsonSerializer.Serialize(command));
                    Log.Information("[JSON] 下发阈值至 {Port}: {Value}°C", portName, value);
                    break;
                }
                case CommunicationProtocol.Modbus:
                {
                    var modbus = (ModbusProtocolService)
                        _serviceProvider.GetRequiredKeyedService<IProtocolParser>("Modbus");
                    await modbus.WriteSingleRegisterAsync(portName,
                        AppConstants.DefaultModbusSlaveAddress, 0x0002, (ushort)Math.Round(value * 10));
                    Log.Information("[Modbus] 下发阈值至 {Port}: {Value}°C (寄存器 {Raw})",
                        portName, value, (ushort)Math.Round(value * 10));
                    break;
                }
            }
        }
    }

    public async Task ResetDeviceAsync()
    {
        string? port = _connectionVm.SelectedPort;
        if (string.IsNullOrEmpty(port)) return;

        var protocol = _connectionVm.SelectedProtocol;
        switch (protocol)
        {
            case CommunicationProtocol.JSON:
            {
                var command = new CommandPayload(
                    CommandId: Guid.NewGuid(),
                    DeviceId: AppConstants.DefaultDeviceName,
                    Action: DeviceAction.Reset,
                    TimestampUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                await _serialPortService.WriteStringAsync(port, JsonSerializer.Serialize(command));
                Log.Information("[{Protocol}] 复位指令已发送至 {Port}", protocol, port);
                break;
            }
            case CommunicationProtocol.Modbus:
            {
                var modbus = (ModbusProtocolService)
                    _serviceProvider.GetRequiredKeyedService<IProtocolParser>("Modbus");
                await modbus.WriteSingleRegisterAsync(port,
                    AppConstants.DefaultModbusSlaveAddress, 0x0001, 1);
                Log.Information("[Modbus] 复位指令已发送至 {Port}", port);
                break;
            }
        }
    }
}
