using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Data.Retention;
using SmartEdgeHMI.Extensions;
using SmartEdgeHMI.Utils.Logging;
using SmartEdgeHMI.Views;

namespace SmartEdgeHMI;

public partial class App : Application
{
    private Mutex? _appMutex;
    private const string AppGuid = "Global\\SmartEdgeHMI_Unique_Application_Mutex_Guid_2026";

    private const string ZhCnCulture = "zh-CN";
    private const string EnUsCulture = "en-US";

    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static IConfiguration Configuration { get; private set; } = null!;

    public static readonly ICommand ToggleLanguageCommand = new RelayCommand(
        () => LocalizationService.Instance.SetLanguage(
            LocalizationService.Instance.CurrentCulture.Name == ZhCnCulture ? EnUsCulture : ZhCnCulture));

    public static string LanguageButtonText =>
        LocalizationService.Instance.CurrentCulture.Name == ZhCnCulture ? "EN" : "中文";

    private sealed class RelayCommand(Action execute) : ICommand
    {
#pragma warning disable CS0067

        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }

    public App()
    {
        ConfigureLogging();
    }

    private static void ConfigureLogging()
    {
#if DEBUG
        Serilog.Debugging.SelfLog.Enable(msg => System.Diagnostics.Debug.WriteLine(msg));
#endif

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.Async(a => a.File(
                path: "logs/log-.txt",
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 31))
            .WriteTo.Sink(new WpfSerilogSink())
            .CreateLogger();
    }

    private static void SetStaticDependencies(IConfiguration configuration, IServiceProvider serviceProvider)
    {
        Configuration = configuration;
        ServiceProvider = serviceProvider;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _appMutex = new Mutex(true, AppGuid, out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("SmartEdgeHMI 系统已在运行中, 请勿重复启动！", "系统提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddAppServices()
            .BuildServiceProvider();

        // 间接安全地初始化静态依赖
        SetStaticDependencies(config, services);

        // 启动配置校验: 服务容器构建完成后、数据库初始化前拦截非法参数(如 0 波特率), 避免带病启动。
        // Environment.Exit 不走 OnExit, 已获取的 Mutex 由 OS 在进程退出时释放。
        var settingsErrors = AppSettingsValidator.Validate(
            ServiceProvider.GetRequiredService<ISettingsService>().Current);
        if (settingsErrors.Count > 0)
        {
            foreach (string error in settingsErrors)
                Log.Fatal("设置校验失败: {Error}", error);

            MessageBox.Show(
                "以下启动配置参数非法:\n" + string.Join("\n", settingsErrors)
                + "\n\n请检查 %LOCALAPPDATA%\\SmartEdgeHMI\\settings.json, 修正或删除该文件后重启。",
                "配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
            return;
        }

        Log.Information("设置校验通过");

        ServiceProvider.GetRequiredService<SqliteConnectionFactory>()
            .InitializeDatabaseAsync()
            .GetAwaiter().GetResult();

        // 启动数据保留策略后台清理(Start 内部自建循环, 此处不 await, 失败不影响主程序)
        ServiceProvider.GetRequiredService<IDataRetentionService>().Start();

        // 启动 Watchdog 心跳客户端
        var heartbeatClient = ServiceProvider.GetRequiredService<WatchdogHeartbeatClient>();
        heartbeatClient.Start();

        LocalizationService.Instance.SetLanguage(ZhCnCulture);

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        Log.Information("SmartEdgeHMI 应用程序已成功启动。");
    }

    private static void App_DispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "【UI主线程】遇到未处理异常");

        if (e.Exception is OutOfMemoryException or AccessViolationException)
        {
            e.Handled = false;
            MessageBox.Show($"发生严重的不可恢复错误, 程序即将关闭。\n异常信息: {e.Exception.Message}", "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Log.CloseAndFlush();
            return;
        }

        e.Handled = true;
        MessageBox.Show($"程序遇到意外错误, 已记录到日志, 请检查操作。\n摘要: {e.Exception.Message}", "意外错误", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;

        if (e.IsTerminating)
        {
            Log.Fatal(ex, "【非UI后台线程】发生致命未处理异常, 进程即将被 CLR 终止！");
            MessageBox.Show($"系统后台线程发生致命错误, 程序即将被迫关闭！\n请将此屏幕截图给技术人员。\n错误信息: {ex?.Message}", "系统崩溃 (Fatal)", MessageBoxButton.OK, MessageBoxImage.Error);
            Log.CloseAndFlush();
        }
        else
        {
            Log.Error(ex, "【非UI后台线程】发生未处理异常");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "【Task异步任务】未观察到异常 (UnobservedTaskException)");
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("SmartEdgeHMI 应用程序正在退出。退出码: {Code}", e.ApplicationExitCode);

            var heartbeatClient = ServiceProvider.GetService<WatchdogHeartbeatClient>();
            heartbeatClient?.Dispose();

            // 数据保留服务与遥测仓储一并收尾, 合计最多等 5 秒, 异常只记日志不阻断退出
            var retentionService = ServiceProvider.GetService<IDataRetentionService>() as IAsyncDisposable;
            var telemetryRepo = ServiceProvider.GetService<ITelemetryRepository>() as IAsyncDisposable;

            if (retentionService is not null || telemetryRepo is not null)
            {
                Task.Run(async () =>
                {
                    if (retentionService is not null)
                    {
                        try { await retentionService.DisposeAsync(); }
                        catch (Exception ex) { Log.Warning(ex, "[Retention] 服务退出清理异常"); }
                    }

                    if (telemetryRepo is not null)
                    {
                        try { await telemetryRepo.DisposeAsync(); }
                        catch (Exception ex) { Log.Warning(ex, "[TelemetryRepo] 退出清理异常"); }
                    }
                }).Wait(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Log.CloseAndFlush();
            try
            {
                _appMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 当前线程未拥有该 Mutex 或其已被自动释放, 安全忽略。
            }
            _appMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
