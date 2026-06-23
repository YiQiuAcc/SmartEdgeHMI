using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.Communication;
using SmartEdgeHMI.Communication.Ports;
using SmartEdgeHMI.Communication.Protocols;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Infrastructure;
using SmartEdgeHMI.State;
using SmartEdgeHMI.ViewModels;
using SmartEdgeHMI.Views;

namespace SmartEdgeHMI.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<SqliteRepository>();
        services.AddSingleton<ITelemetryRepository>(sp => sp.GetRequiredService<SqliteRepository>());
        services.AddSingleton<IAlarmRepository>(sp => sp.GetRequiredService<SqliteRepository>());
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
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<WatchdogHeartbeatClient>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
