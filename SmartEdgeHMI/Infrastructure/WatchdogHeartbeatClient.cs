using System.IO;
using System.IO.Pipes;
using Serilog;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Infrastructure;

/// <summary>Watchdog 心跳客户端: 通过命名管道定期发送心跳, Watchdog 超时未收到则判定 HMI 异常并重启</summary>
public sealed class WatchdogHeartbeatClient : IDisposable
{
    private const string PipeName = "SmartEdgeHMI_Watchdog_Pipe";

    private readonly CancellationTokenSource _cts = new();

    /// <summary>启动心跳发送后台任务, Watchdog 未运行时内部自动重连</summary>
    public void Start()
    {
        _ = Task.Run(() => RunAsync(_cts.Token));
        Log.Information("Watchdog 心跳发送已启动 (间隔 {Interval}ms)", AppConstants.WatchdogHeartbeatIntervalMs);
    }

    /// <summary>后台主管道连接循环</summary>
    private static async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipeClient = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);

                await pipeClient.ConnectAsync(AppConstants.WatchdogConnectTimeoutMs, ct);
                Log.Debug("已连接到 Watchdog 命名管道");
                await SendHeartbeatLoopAsync(pipeClient, ct);
            }
            catch (TimeoutException)
            {
                Log.Debug("Watchdog 未运行或未就绪, {Interval}ms 后重试...", AppConstants.WatchdogHeartbeatIntervalMs);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warning(ex, "心跳发送异常, {Interval}ms 后重试", AppConstants.WatchdogHeartbeatIntervalMs);
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(AppConstants.WatchdogReconnectDelayMs, ct);
            }
        }
    }

    /// <summary>心跳发送循环</summary>
    private static async Task SendHeartbeatLoopAsync(NamedPipeClientStream pipeClient, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AppConstants.WatchdogHeartbeatIntervalMs));
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
