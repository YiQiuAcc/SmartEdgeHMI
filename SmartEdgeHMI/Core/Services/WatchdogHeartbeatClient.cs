using System.IO;
using System.IO.Pipes;
using Serilog;

namespace SmartEdgeHMI.Core.Services;

/// <summary>Watchdog 心跳客户端: 通过命名管道定期发送心跳, Watchdog 超时未收到则判定 HMI 异常并重启</summary>
/// <remarks>构造心跳客户端</remarks>
public sealed class WatchdogHeartbeatClient(ISettingsService settingsService) : IDisposable
{
    private const string PipeName = "SmartEdgeHMI_Watchdog_Pipe";

    private readonly CancellationTokenSource _cts = new();

    /// <summary>启动心跳发送后台任务, Watchdog 未运行时内部自动重连</summary>
    public void Start()
    {
        _ = Task.Run(() => RunAsync(_cts.Token));
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
                    ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);

                await pipeClient.ConnectAsync(settingsService.Current.Watchdog.ConnectTimeoutMs, ct);
                Log.Debug("已连接到 Watchdog 命名管道");
                await SendHeartbeatLoopAsync(pipeClient, ct);
            }
            catch (TimeoutException)
            {
#if DEBUG

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
