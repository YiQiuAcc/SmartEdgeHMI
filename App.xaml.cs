using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Extensions;
using SmartEdgeHMI.Services;
using SmartEdgeHMI.Views;

namespace SmartEdgeHMI;

public partial class App : Application
{
    private static Mutex? _appMutex;
    private const string AppGuid = "Global\\SmartEdgeHMI_Unique_Application_Mutex_Guid_2026";
    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static IConfiguration Configuration { get; private set; } = null!;

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
                rollOnFileSizeLimit: true
,
                retainedFileCountLimit: 31))
            .WriteTo.Sink(new WpfSerilogSink())
            .CreateLogger();
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
        // 注册事件
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // 全局配置
        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        // 注册并构建服务容器
        ServiceProvider = new ServiceCollection()
            .AddSingleton(Configuration)
            .AddAppServices()
            .BuildServiceProvider();

        // 初始化数据库
        ServiceProvider.GetRequiredService<SqliteRepository>()
            .InitializeDatabaseAsync()
            .GetAwaiter().GetResult();

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        Log.Information("SmartEdgeHMI 应用程序已成功启动。");
    }

    private void App_DispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
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

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
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

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "【Task异步任务】未观察到异常 (UnobservedTaskException)");
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information($"SmartEdgeHMI 应用程序正常退出。退出码: {e.ApplicationExitCode}");
        }
        finally
        {
            Log.CloseAndFlush();
            try { _appMutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _appMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
