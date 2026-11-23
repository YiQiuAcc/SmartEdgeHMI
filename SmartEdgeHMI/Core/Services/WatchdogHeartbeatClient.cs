using System.IO;
using System.IO.Pipes;
using Serilog;
using SmartEdgeHMI.Shared;
using SmartEdgeHMI.Utils;

namespace SmartEdgeHMI.Core.Services;

/// <summary>Watchdog 心跳客户端: 经命名管道按固定间隔发送心跳, Watchdog 超时未收到即判定 HMI 无响应并重启进程</summary>
/// <remarks>
/// 管道断开视为 Watchdog 未运行, 按 <c>ReconnectDelayMs</c> 无限重连, 不向调用方冒泡异常。
/// <see cref="Dispose"/> 只取消不等待: 在飞的心跳任务在下一次检查点时退出, 其异常由 TaskExtensions 记录。
/// </remarks>
public sealed class WatchdogHeartbeatClient(ISettingsService settingsService) : IDisposable
{
    // 管道名按程序所在目录派生, 与 Watchdog 侧同一规则; 静态只读保证只算一次 SHA256
    private static readonly string _pipeName = WatchdogPipeName.For(AppContext.BaseDirectory);

    private readonly CancellationTokenSource _cts = new();

    /// <summary>启动心跳发送后台任务, Watchdog 未运行时内部自动重连</summary>
    public void Start()
    {
        _ = Task.Run(() => RunAsync(_cts.Token))
            .ObserveAndLogAsync("Watchdog 心跳任务");
        Log.Information("Watchdog 心跳发送已启动 (间隔 {Interval}ms)", settingsService.Current.Watchdog.HeartbeatIntervalMs);
    }

    /// <summary>后台主管道连接循环</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipeClient = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);

                await pipeClient.ConnectAsync(settingsService.Current.Watchdog.ConnectTimeoutMs, ct);
                Log.Debug("已连接到 Watchdog 命名管道");
                await SendHeartbeatLoopAsync(pipeClient, ct);
            }
            catch (TimeoutException)
            {
#if DEBUG
                // 调试构建下不输出: 本地调试一般不启动 Watchdog, 逐轮记录会掩盖有用输出
#else
                Log.Debug("Watchdog 未运行或未就绪, {Interval}ms 后重试...", settingsService.Current.Watchdog.HeartbeatIntervalMs);
#endif
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warning(ex, "心跳发送异常, {Interval}ms 后重试", settingsService.Current.Watchdog.HeartbeatIntervalMs);
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(settingsService.Current.Watchdog.ReconnectDelayMs, ct);
            }
        }
    }

    /// <summary>心跳发送循环</summary>
    private async Task SendHeartbeatLoopAsync(NamedPipeClientStream pipeClient, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settingsService.Current.Watchdog.HeartbeatIntervalMs));
        byte[] heartbeat = "hb"u8.ToArray();

        while (!ct.IsCancellationRequested && pipeClient.IsConnected)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
                await pipeClient.WriteAsync(heartbeat, ct);
                await pipeClient.FlushAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                Log.Debug("Watchdog 管道连接断开, 将在下次轮询时重连");
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
