using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Protocols.Parsers;
using SmartEdgeHMI.Protocols.Parsers.Modbus;
using SmartEdgeHMI.Protocols.Transports;

namespace SmartEdgeHMI.Protocols;

/// <summary>设备通信协调器。 职责: 指令下发(阈值设置、设备复位)+ 设备状态变更路由(通知所有协议解析器)</summary>
public class DeviceCommunicationCoordinator : IDeviceCommunicationCoordinator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ITransportService _transport;
    private readonly IProtocolConfig _protocolConfig;

    public DeviceCommunicationCoordinator(
        IServiceProvider serviceProvider,
        ITransportService transport,
        IProtocolConfig protocolConfig)
    {
        _serviceProvider = serviceProvider;
        _transport = transport;
        _protocolConfig = protocolConfig;

        _transport.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(string portName, ConnectionState state)
    {
        NotifyParser("JSON", portName, state);
        NotifyParser("Modbus", portName, state);
    }

    private void NotifyParser(string key, string portName, ConnectionState state)
    {
        try
        {
            var parser = _serviceProvider.GetKeyedService<IProtocolParser>(key);
            parser?.OnDeviceStateChanged(portName, state);
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
                    await _transport.WriteStringAsync(portName, JsonSerializer.Serialize(command));
                    Log.Information("[JSON] 下发阈值至 {Port}: {Value}°C", portName, value);
                    break;
                }
                case CommunicationProtocol.Modbus:
                {
                    var modbus = (ModbusProtocolParser)
                        _serviceProvider.GetRequiredKeyedService<IProtocolParser>("Modbus");
                    await modbus.WriteSingleRegisterAsync(portName,
                        _protocolConfig.SlaveAddress, ModbusProtocolParser.RegisterThreshold, (ushort)Math.Round(value * 10));
                    Log.Information("[Modbus] 下发阈值至 {Port}: {Value}°C (地址 0x{Addr:X4} ← 写入值 {Raw})",
                        portName, value, ModbusProtocolParser.RegisterThreshold, (ushort)Math.Round(value * 10));
                    break;
                }
            }
        }
    }

    /// <summary>异步复位设备, 根据当前协议类型选择 JSON 或 Modbus 指令格式</summary>
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
                await _transport.WriteStringAsync(port, JsonSerializer.Serialize(command));
                Log.Information("[{Protocol}] 复位指令已发送至 {Port}", protocol, port);
                break;
            }
            case CommunicationProtocol.Modbus:
            {
                var modbus = (ModbusProtocolParser)
                    _serviceProvider.GetRequiredKeyedService<IProtocolParser>("Modbus");
                await modbus.WriteSingleRegisterAsync(port,
                    _protocolConfig.SlaveAddress, ModbusProtocolParser.RegisterReset, 1);
                Log.Information("[Modbus] 复位指令已发送至 {Port}", port);
                break;
            }
        }
    }
}
