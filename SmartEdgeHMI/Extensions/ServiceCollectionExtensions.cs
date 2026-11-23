using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Data.Retention;
using SmartEdgeHMI.Protocols;
using SmartEdgeHMI.Protocols.Parsers;
using SmartEdgeHMI.Protocols.Parsers.Json;
using SmartEdgeHMI.Protocols.Parsers.Modbus;
using SmartEdgeHMI.Protocols.Transports;
using SmartEdgeHMI.Protocols.Transports.Serial;
using SmartEdgeHMI.ViewModels;
using SmartEdgeHMI.Views;

namespace SmartEdgeHMI.Extensions;

/// <summary>应用服务注册: 全部为单例</summary>
/// <remarks>
/// 单例是刻意的: 进程内只有一条串口链路、一个数据库和一个主窗口, 状态必须共享。
/// 注意两处别名注册 <see cref="ITransportService"/> 指向同一个 <see cref="ISerialPortService"/>,
/// <see cref="IProtocolConfig"/> 指向同一个 <see cref="ConnectionViewModel"/>, 拆开会造成状态分裂。
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 HMI 运行期所需的全部服务</summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddSingleton<ISerialPortService, SerialPortService>();
        // 传输层与串口服务必须是同一实例, 否则协议层写入的上下文与传输层持有的不同
        services.AddSingleton<ITransportService>(sp => sp.GetRequiredService<ISerialPortService>());
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<ITelemetryRepository, SqliteTelemetryRepository>();
        services.AddSingleton<IAlarmRepository, SqliteAlarmRepository>();
        services.AddSingleton<IDataRetentionService, DataRetentionService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDeviceStateContainer, DeviceStateContainer>();
        // 解析器按协议键注册, 同名接口两个实现, 由 SerialPortService 按当前协议解析
        services.AddKeyedSingleton<IProtocolParser, JsonProtocolParser>("JSON");
        services.AddKeyedSingleton<IProtocolParser, ModbusProtocolParser>("Modbus");
        services.AddSingleton<IDeviceCommunicationCoordinator, DeviceCommunicationCoordinator>();
        services.AddSingleton<IAlarmStateMachine, AlarmStateMachine>();
        services.AddSingleton<ConnectionViewModel>();
        // 协议配置由连接页视图模型提供, 同一实例同时扮演 ViewModel 与配置源
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
