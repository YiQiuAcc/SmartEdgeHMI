using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.Database;
using SmartEdgeHMI.Database.Repositories;
using SmartEdgeHMI.Protocols.Ports;
using SmartEdgeHMI.Utils;
using SmartEdgeHMI.Protocols;
using SmartEdgeHMI.Protocols.Services;
using SmartEdgeHMI.MachineState;
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
        services.AddKeyedSingleton<IProtocolParser, JsonProtocolService>("JSON");
        services.AddKeyedSingleton<IProtocolParser, ModbusProtocolService>("Modbus");
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
