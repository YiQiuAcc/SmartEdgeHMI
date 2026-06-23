using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Communication.Ports;
using SmartEdgeHMI.Communication.Protocols;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Communication;

/// <summary>
/// 设备通信协调器。 职责：指令下发（阈值设置、设备复位）+ 设备状态变更路由（通知所有协议解析器）。 原始字节流的路由已下沉到
/// SerialPortService.ForwardDataLoopAsync。
/// </summary>
public class DeviceCommunicationCoordinator : IDeviceCommunicationCoordinator,
    IRecipient<DeviceStateChanged>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISerialPortService _serialPortService;
    private readonly IProtocolConfig _protocolConfig;

    public DeviceCommunicationCoordinator(
        IServiceProvider serviceProvider,
        ISerialPortService serialPortService,
        IProtocolConfig protocolConfig)
    {
        _serviceProvider = serviceProvider;
        _serialPortService = serialPortService;
        _protocolConfig = protocolConfig;
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    void IRecipient<DeviceStateChanged>.Receive(DeviceStateChanged message)
    {
        NotifyParser("JSON", message);
        NotifyParser("Modbus", message);
    }

    private void NotifyParser(string key, DeviceStateChanged message)
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
        var protocol = _protocolConfig.SelectedProtocol;
        foreach (string portName in _protocolConfig.ConnectedPorts.ToList())
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
                        AppConstants.DefaultModbusSlaveAddress, ModbusProtocolService.RegisterThreshold, (ushort)Math.Round(value * 10));
                    Log.Information("[Modbus] 下发阈值至 {Port}: {Value}°C (寄存器 {Raw})",
                        portName, value, (ushort)Math.Round(value * 10));
                    break;
                }
            }
        }
    }

    public async Task ResetDeviceAsync()
    {
        string? port = _protocolConfig.SelectedPort;
        if (string.IsNullOrEmpty(port)) return;

        var protocol = _protocolConfig.SelectedProtocol;
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
                    AppConstants.DefaultModbusSlaveAddress, ModbusProtocolService.RegisterReset, 1);
                Log.Information("[Modbus] 复位指令已发送至 {Port}", port);
                break;
            }
        }
    }
}
