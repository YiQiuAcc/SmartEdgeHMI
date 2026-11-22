using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Serilog;
using SmartEdgeHMI.Shared;

namespace SmartEdgeHMI.Watchdog;

/// <summary>
/// SmartEdgeHMI Watchdog 守护进程
/// 1. 通过命名管道监听 HMI 主进程的心跳信号
/// 2. 如果心跳超时(HMI 进程无响应或崩溃), 自动重新启动 HMI
/// 3. 记录自身运行日志, 避免与 HMI 日志文件冲突
/// </summary>
public static class Program
{
    // 心跳管道名按被测程序所在目录派生(见 WatchdogPipeName), 在 Main 中路径校验通过后赋值
    private static string _pipeName = string.Empty;
    private const string MutexName = "Global\\SmartEdgeHMI_Watchdog_Mutex";

    // 心跳超时阈值(毫秒)
    private const int HeartbeatTimeoutMs = 10_000;
    // Watchdog 检测周期(毫秒)
    private const int WatchdogTickMs = 2_000;

    // 管道服务端当前是否已成功创建(处于等待连接/读取心跳状态); 只有它为真时才允许按心跳超时杀进程
    private static volatile bool _pipeServerBound;
    // 上次打印管道绑定失败 ERR 日志的时刻, 用于把重复报错降噪为 60 秒一条
    private static DateTime _lastPipeBindErrorLog = DateTime.MinValue;

    private static DateTime _lastHeartbeat = DateTime.MinValue;
    private static Process? _hmiProcess;
    private static string? _hmiExecutablePath;
    private static string? _hmiWorkingDirectory;

    public static async Task Main(string[] args)
    {
        // 只运行一个 Watchdog 实例
        using var mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            await Console.Out.WriteLineAsync("Watchdog 已在运行中, 退出。");
            return;
        }

        ConfigureLogging();

        // 解析 HMI 可执行文件路径
        string currentDir = AppContext.BaseDirectory;

        if (args.Length >= 1 && File.Exists(args[0]))
        {
            _hmiExecutablePath = Path.GetFullPath(args[0]);
        }
        else
        {
            _hmiExecutablePath = Path.Combine(currentDir, "SmartEdgeHMI.exe");

            if (!File.Exists(_hmiExecutablePath))
            {
#if DEBUG
                _hmiExecutablePath = Path.GetFullPath(
                    Path.Combine(currentDir, "..", "..", "..", "..", "SmartEdgeHMI", "bin", "Debug", "net8.0-windows", "SmartEdgeHMI.exe"));
#else
    string productionFallback = Path.GetFullPath(Path.Combine(currentDir, "..", "SmartEdgeHMI.exe"));
    if (File.Exists(productionFallback))
    {
        _hmiExecutablePath = productionFallback;
    }
#endif
            }
        }
        _hmiWorkingDirectory = Path.GetDirectoryName(_hmiExecutablePath);

        if (string.IsNullOrEmpty(_hmiExecutablePath) || !File.Exists(_hmiExecutablePath))
        {
            Log.Error("无法找到 HMI 主程序可执行文件, 请通过命令行参数指定路径。");
            await Console.Error.WriteLineAsync("用法: SmartEdgeHMI.Watchdog.exe [HMI可执行文件路径]");
            return;
        }

        // 管道名必须在启动 HMI 之前确定: 主程序按同一规则派生管道名才能连上本进程
        _pipeName = WatchdogPipeName.For(_hmiWorkingDirectory);

        // 合并日志调用, 满足单代码块少于 3 个 Information 日志的限制
        Log.Information("Watchdog 守护进程启动。监控目标: {Path}, 工作目录: {Dir}, 管道: {Pipe}", _hmiExecutablePath, _hmiWorkingDirectory, _pipeName);

        // 启动 HMI 主进程
        StartHmiProcess();

        using var heartbeatCts = new CancellationTokenSource();
        // 监听控制台取消事件 (Ctrl+C)
        Console.CancelKeyPress += async (_, e) =>
        {
            e.Cancel = true;
            Log.Information("收到退出信号, 正在关闭守护进程...");
            await heartbeatCts.CancelAsync();
        };

        Task pipeTask = RunPipeServerAsync(heartbeatCts.Token);
        Task monitorTask = RunMonitorLoopAsync(heartbeatCts.Token);

        try
        {
            await Task.WhenAll(pipeTask, monitorTask);
        }
        catch (OperationCanceledException ex)
        {
            Log.Debug(ex, "Watchdog 核心任务已被主动取消。");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Watchdog 遭遇未处理异常, 退出。");
        }

        Log.Information("Watchdog 守护进程退出。");
    }

    private static void ConfigureLogging()
    {
        string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Async(a => a.File(
                path: Path.Combine(logDir, "watchdog-.log"),
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7))
            .CreateLogger();
    }

    /// <summary>启动 HMI 主进程</summary>
    private static void StartHmiProcess()
    {
        try
        {
            _hmiProcess?.Dispose();

            var psi = new ProcessStartInfo
            {
                FileName = _hmiExecutablePath!,
                WorkingDirectory = _hmiWorkingDirectory ?? Path.GetDirectoryName(_hmiExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _hmiProcess = Process.Start(psi);
            _lastHeartbeat = DateTime.UtcNow;

            if (_hmiProcess is not null)
            {
                Log.Information("HMI 主进程已启动, PID: {Pid}", _hmiProcess.Id);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Watchdog 尝试从路径启动 HMI 失败。当前配置路径: {_hmiExecutablePath}", ex);
        }
    }

    /// <summary>命名管道服务端循环</summary>
    private static async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 每轮开始先回到未绑定状态, 直到管道服务端真正创建成功才允许按心跳超时杀进程
            _pipeServerBound = false;
            try
            {
                await using var pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);
                _pipeServerBound = true;

                Log.Debug("Watchdog 等待 HMI 连接命名管道...");

                // 等待客户端连接
                if (!await WaitForConnectionAsync(pipeServer, ct))
                {
                    return;
                }
                Log.Information("HMI 已连接到 Watchdog 命名管道。");
                // 处理心跳读取循环
                await HandlePipeReaderLoopAsync(pipeServer, ct);
            }
            catch (OperationCanceledException ex)
            {
                Log.Debug(ex, "管道服务端由于取消操作正常退出。");
                return;
            }
            catch (Exception ex)
            {
                // 绑定失败常见于管道名被其他进程占用, 每 2 秒刷一条 ERR 会淹没日志:
                // 距上次打印不足 60 秒时降为 Debug, 避免 ERROR 级别刷屏
                if (DateTime.UtcNow - _lastPipeBindErrorLog >= TimeSpan.FromSeconds(60))
                {
                    _lastPipeBindErrorLog = DateTime.UtcNow;
                    Log.Error(ex, "管道服务器循环异常, 将在 2 秒后重试。");
                }
                else
                {
                    Log.Debug(ex, "管道服务器循环异常, 将在 2 秒后重试。");
                }

                await Task.Delay(2000, ct);
            }
            finally
            {
                // 连接结束或异常后回到未绑定状态, 保证超时判定不基于失效的心跳通道
                _pipeServerBound = false;
            }
        }
    }

    /// <summary>等待命名的管道连接</summary>
    private static async Task<bool> WaitForConnectionAsync(NamedPipeServerStream pipeServer, CancellationToken ct)
    {
        var connectTask = pipeServer.WaitForConnectionAsync(ct);
        while (!connectTask.IsCompleted)
        {
            await Task.Delay(1000, ct);
            if (ct.IsCancellationRequested) return false;
        }
        await connectTask;
        return true;
    }

    /// <summary>处理管道内数据读取循环</summary>
    private static async Task HandlePipeReaderLoopAsync(NamedPipeServerStream pipeServer, CancellationToken ct)
    {
        byte[] buffer = new byte[32];
        while (!ct.IsCancellationRequested && pipeServer.IsConnected)
        {
            try
            {
                int bytesRead = await pipeServer.ReadAsync(buffer, ct);
                if (bytesRead > 0)
                {
                    string heartbeat = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\0');
                    _lastHeartbeat = DateTime.UtcNow;
                    Log.Debug("[Heartbeat] 收到心跳: {Beat}", heartbeat);
                }
                else
                {
                    Log.Warning("HMI 关闭了管道连接, 等待重连...");
                    break;
                }
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "管道读取异常, HMI 可能已断开。");
                break;
            }
        }
    }

    /// <summary>监控循环: 定期检测心跳是否超时</summary>
    private static async Task RunMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchdogTickMs, ct);

                TimeSpan elapsedSinceHeartbeat = DateTime.UtcNow - _lastHeartbeat;

                // 如果 HMI 进程已退出, 立即重启
                if (_hmiProcess?.HasExited == true)
                {
                    Log.Warning("HMI 主进程已退出 (ExitCode: {Code}), 正在重启...", _hmiProcess.ExitCode);
                    StartHmiProcess();
                    continue;
                }

                // 如果超过心跳超时阈值且 HMI 仍在运行, 判定为无响应, 强制重启。
                // 只有管道已绑定才允许按超时杀进程: 管道未绑定时无法区分「主程序无响应」与「心跳通道根本不可用」,
                // 此时杀进程只会把健康进程反复重启, 属于雪崩放大
                if (_pipeServerBound && _lastHeartbeat != DateTime.MinValue && elapsedSinceHeartbeat.TotalMilliseconds > HeartbeatTimeoutMs)
                {
                    Log.Warning("HMI 心跳超时 ({Seconds:F1}s 无心跳), 正在强制重启...", elapsedSinceHeartbeat.TotalSeconds);

                    KillHmiProcess();
                    await Task.Delay(1000, ct);
                    StartHmiProcess();
                }
            }
            catch (OperationCanceledException ex)
            {
                Log.Debug(ex, "监控循环已被正常取消。");
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "监控循环异常");
            }
        }
    }

    /// <summary>强制终止 HMI 进程</summary>
    private static void KillHmiProcess()
    {
        if (_hmiProcess?.HasExited != false) return;

        try
        {
            if (_hmiProcess.CloseMainWindow())
            {
                Log.Information("已发送关闭窗口消息到 HMI 进程 (PID: {Pid})", _hmiProcess.Id);
                if (_hmiProcess.WaitForExit(3000))
                {
                    return;
                }
            }

            _hmiProcess.Kill(entireProcessTree: true);
            Log.Warning("已强制终止 HMI 进程树 (PID: {Pid})", _hmiProcess.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "终止 HMI 进程时发生异常");
        }
    }
}
