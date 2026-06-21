using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Services;

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

    // 入站路由：根据当前协议将原始字节转发给对应的解析器
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

    // 状态变更路由：同时通知两个解析器以确保非活跃解析器的 Pipe 也能被清理
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

    // 出站命令（保留 switch，通过 Keyed DI 解析具体实现）
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
                    Log.Information("[JSON] 阈值配置已下发至 {Port}: {Value}°C", portName, value);
                    break;
                }
                case CommunicationProtocol.Modbus:
                {
                    var modbus = (ModbusProtocolService)
                        _serviceProvider.GetRequiredKeyedService<IProtocolParser>("Modbus");
                    await modbus.WriteSingleRegisterAsync(portName,
                        AppConstants.DefaultModbusSlaveAddress, 0x0002, (ushort)Math.Round(value * 10));
                    Log.Information("[Modbus] 阈值配置已下发至 {Port}: {Value}°C (寄存器值: {Raw})",
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
                Log.Information("[{Protocol}] 设备复位指令已发送至 {Port}", protocol, port);
                break;
            }
            case CommunicationProtocol.Modbus:
            {
                var modbus = (ModbusProtocolService)
                    _serviceProvider.GetRequiredKeyedService<IProtocolParser>("Modbus");
                await modbus.WriteSingleRegisterAsync(port,
                    AppConstants.DefaultModbusSlaveAddress, 0x0001, 1);
                Log.Information("[{Protocol}] Modbus 复位指令已发送至 {Port}", protocol, port);
                break;
            }
        }
    }
}
