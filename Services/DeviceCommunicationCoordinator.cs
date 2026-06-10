using System.Text.Json;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Services;

public class DeviceCommunicationCoordinator : IDeviceCommunicationCoordinator
{
    private readonly ISerialPortService _serialPortService;
    private readonly ModbusProtocolService _modbusService;
    private readonly ConnectionViewModel _connectionVm;

    public DeviceCommunicationCoordinator(
        ISerialPortService serialPortService,
        ModbusProtocolService modbusService,
        ConnectionViewModel connectionVm)
    {
        _serialPortService = serialPortService;
        _modbusService = modbusService;
        _connectionVm = connectionVm;
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
                    Log.Information("[JSON] 阈值配置已下发至 {Port}: {Value}°C", portName, value);
                    break;
                }
                case CommunicationProtocol.Modbus:
                {
                    await _modbusService.WriteSingleRegisterAsync(portName,
                        AppConstants.DefaultModbusSlaveAddress, 0x0002, (ushort)value);
                    Log.Information("[Modbus] 阈值配置已下发至 {Port}: {Value}°C", portName, (ushort)value);
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
                await _modbusService.WriteSingleRegisterAsync(port,
                    AppConstants.DefaultModbusSlaveAddress, 0x0001, 1);
                Log.Information("[{Protocol}] Modbus 复位指令已发送至 {Port}", protocol, port);
                break;
            }
        }
    }
}
