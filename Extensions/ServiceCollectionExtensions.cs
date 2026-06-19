using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.Services;
using SmartEdgeHMI.ViewModels;
using SmartEdgeHMI.Views;

namespace SmartEdgeHMI.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<ISqliteRepository, SqliteRepository>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<JsonProtocolService>();
        services.AddSingleton<ModbusProtocolService>();
        services.AddSingleton<IDeviceCommunicationCoordinator, DeviceCommunicationCoordinator>();
        services.AddSingleton<IAlarmStateMachine, AlarmStateMachine>();
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<MonitorViewModel>();
        services.AddSingleton<AlarmHistoryViewModel>();
        services.AddSingleton<LogConsoleViewModel>();
        services.AddSingleton<TrendViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
