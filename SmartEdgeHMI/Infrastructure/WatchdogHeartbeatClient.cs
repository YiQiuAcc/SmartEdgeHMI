using System.IO;
using System.IO.Pipes;
using Serilog;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Infrastructure;

public sealed class WatchdogHeartbeatClient : IDisposable
{
    private const string PipeName = "SmartEdgeHMI_Watchdog_Pipe";

    private readonly CancellationTokenSource _cts = new();
    private Task? _heartbeatTask;

    public void Start()
    {
        _heartbeatTask = Task.Run(() => RunAsync(_cts.Token));
        Log.Information("Watchdog 心跳发送已启动 (间隔 {Interval}ms)", AppConstants.WatchdogHeartbeatIntervalMs);
    }

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

                var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AppConstants.WatchdogHeartbeatIntervalMs));
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
                timer.Dispose();
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
                await Task.Delay(AppConstants.WatchdogReconnectDelayMs, ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
