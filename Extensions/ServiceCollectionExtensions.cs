using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.Services;
using SmartEdgeHMI.Views;

namespace SmartEdgeHMI.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<ISqliteRepository, SqliteRepository>();
        // services.AddSingleton<MainViewModel>();
        // services.AddSingleton<DashboardViewModel>();
        // services.AddTransient<HistoryViewModel>();
        // services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
