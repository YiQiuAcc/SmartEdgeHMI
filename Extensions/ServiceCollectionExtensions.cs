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
        services.AddSingleton<MainViewModel>();
        // services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
