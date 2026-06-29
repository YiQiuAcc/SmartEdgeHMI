using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Protocols;
using SmartEdgeHMI.Protocols.Parsers;
using SmartEdgeHMI.Protocols.Parsers.Json;
using SmartEdgeHMI.Protocols.Parsers.Modbus;
using SmartEdgeHMI.Protocols.Transports;
using SmartEdgeHMI.Protocols.Transports.Serial;
using SmartEdgeHMI.ViewModels;
using SmartEdgeHMI.Views;

namespace SmartEdgeHMI.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<ITransportService>(sp => sp.GetRequiredService<ISerialPortService>());
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<ITelemetryRepository, SqliteTelemetryRepository>();
        services.AddSingleton<IAlarmRepository, SqliteAlarmRepository>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDeviceStateContainer, DeviceStateContainer>();
        services.AddKeyedSingleton<IProtocolParser, JsonProtocolParser>("JSON");
        services.AddKeyedSingleton<IProtocolParser, ModbusProtocolParser>("Modbus");
        services.AddSingleton<IDeviceCommunicationCoordinator, DeviceCommunicationCoordinator>();
        services.AddSingleton<IAlarmStateMachine, AlarmStateMachine>();
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<IProtocolConfig>(sp => sp.GetRequiredService<ConnectionViewModel>());
        services.AddSingleton<MonitorViewModel>();
        services.AddSingleton<AlarmHistoryViewModel>();
        services.AddSingleton<LogConsoleViewModel>();
        services.AddSingleton<TrendViewModel>();
        services.AddSingleton<ChartViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<WatchdogHeartbeatClient>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
