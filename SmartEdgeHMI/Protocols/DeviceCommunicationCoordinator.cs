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

/// <summary>设备通信协调器: 下发控制指令(阈值/复位), 并把传输层的连接状态路由给各协议解析器</summary>
/// <remarks>
/// 状态路由在传输层的事件线程上同步执行, 因此 <see cref="OnStateChanged"/> 链路内不得做阻塞操作。
/// 两种协议共用同一套对外方法, 具体报文格式由当前 <see cref="CommunicationProtocol"/> 决定。
/// </remarks>
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
        // 两个解析器都要收到通知
        NotifyParser("JSON", portName, state);
        NotifyParser("Modbus", portName, state);
    }

    private void NotifyParser(string key, string portName, ConnectionState state)
    {
        try
        {
            // 用 GetKeyedService 未注册该协议的部署不应抛异常
            var parser = _serviceProvider.GetKeyedService<IProtocolParser>(key);
            parser?.OnDeviceStateChanged(portName, state);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Coordinator] 通知 {Key} 解析器状态变更失败", key);
        }
    }

    /// <summary>把报警阈值下发到所有已连接端口</summary>
    /// <param name="value">阈值, 单位 °C; Modbus 侧按设备缩放系数 ×10 转为寄存器整数值</param>
    public async Task SendThresholdAsync(double value)
    {
        var protocol = _protocolConfig.SelectedProtocol;
        // 先快照再遍历, 循环体内有 await, 期间界面可能增删连接
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

    /// <summary>复位设备, 按当前协议选择指令格式</summary>
    /// <remarks>只作用于界面选中的端口; 与阈值下发语义不同, 复位不波及未选中的设备</remarks>
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
                    // 固件侧约定: 向寄存器 0x0001 写入 1 触发复位
                    await modbus.WriteSingleRegisterAsync(port,
                        _protocolConfig.SlaveAddress, ModbusProtocolParser.RegisterReset, 1);
                    Log.Information("[Modbus] 复位指令已发送至 {Port}", port);
                    break;
                }
        }
    }
}
