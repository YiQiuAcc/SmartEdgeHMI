using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Protocols.Parsers;
using SmartEdgeHMI.Utils;

namespace SmartEdgeHMI.Protocols.Transports.Serial;

/// <summary>串口物理层: 每个端口一条读循环与一个写锁的全双工模型</summary>
/// <remarks>
/// 读路径刻意使用同步 <c>SerialPort.Read</c> 配合 500ms 的 ReadTimeout 轮询, 而不用 DataReceived 事件或异步重叠 I/O
/// 后者在 USB 转串口设备上存在已知冲突, 会让读循环卡住不返回; 同步读超时则能让循环定时回到取消检查点。
/// 写路径用 <see cref="PortContext.WriteLock"/> 串行化, 避免多线程并发写入导致字节流交错。
/// </remarks>
public class SerialPortService(IServiceProvider serviceProvider) : ISerialPortService, IDisposable
{
    private readonly ConcurrentDictionary<string, PortContext> _activePorts = new();
    private readonly ConcurrentDictionary<string, int> _portBaudRates = new();
    private bool _disposed;

    // 打开或重连后先静默 5s 再上报 Connected, 期间不发起任何读写
    private const int StabilizationDelayMs = 5000;
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectBaseDelayMs = 5000;

    public event Action<string, ConnectionState>? StateChanged;

    // IsOwnedEntry 以本类型为参数, 测试项目需要直接断言引用判定
    internal sealed class PortContext(SerialPort port, CancellationTokenSource cts) : IDisposable
    {
        public SerialPort Port { get; set; } = port;
        public CancellationTokenSource Cts { get; set; } = cts;
        public CancellationToken Token { get; } = cts.Token;
        public SemaphoreSlim WriteLock { get; } = new SemaphoreSlim(1, 1);

        public void Dispose()
        {
            WriteLock.Dispose();
            Cts.Dispose();
        }
    }

    public string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    public void OpenPort(string portName, int baudRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _portBaudRates[portName] = baudRate;
        OpenPortInternal(portName, baudRate);
    }

    private void OpenPortInternal(string portName, int baudRate)
    {
        var serialPort = new SerialPort(portName, baudRate)
        {
            // 500ms 即轮询周期: 决定了取消与断线的最长感知延迟, 也限制了单次阻塞的时长
            ReadTimeout = 500,
            WriteTimeout = SerialPort.InfiniteTimeout,
            DtrEnable = false,
            RtsEnable = false
        };

        try
        {
            serialPort.Open();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] 打开串口 {Port} 失败", portName);
            serialPort.Dispose();
            StateChanged?.Invoke(portName, ConnectionState.Error);
            return;
        }

        Log.Information("[SerialPort] 已打开串口 {Port}, 波特率 {Baud}, DTR={Dtr}, RTS={Rts}",
            portName, baudRate, serialPort.DtrEnable, serialPort.RtsEnable);

        var cts = new CancellationTokenSource();
        var context = new PortContext(serialPort, cts);

        if (_activePorts.TryRemove(portName, out var oldCtx))
        {
            CleanupContext(oldCtx);
        }

        if (!_activePorts.TryAdd(portName, context))
        {
            CloseAndDisposePort(serialPort);
            cts.Dispose();
            return;
        }

        // 读循环独立于调用线程运行, 常规异常在循环内部消化
        _ = Task.Run(() => RunReadLoopWithReconnectAsync(portName, baudRate, context), context.Token)
            .ObserveAndLogAsync($"[SerialPort] {portName} 后台读取任务");
    }

    /// <summary>端口读循环的顶层为打开, 稳定期, 读会话, 断线后按指数退避重连</summary>
    /// <remarks>
    /// attempt 为连续失败计数, 成功度过稳定期即归零;
    /// 任务的生存期与端口上下文一致, 由 <see cref="ClosePort"/> 或 <see cref="Dispose()"/> 终止。
    /// </remarks>
    private async Task RunReadLoopWithReconnectAsync(string portName, int baudRate, PortContext ctx)
    {
        int attempt = 0;

        while (!_disposed && attempt < MaxReconnectAttempts)
        {
            if (attempt > 0)
            {
                bool reopened = await HandleReconnectDelayAndOpenAsync(portName, baudRate, ctx, attempt);
                if (!reopened)
                {
                    // 上下文已被取消代表本会话已被手动重连的新连接或用户断开取代
                    // 立即退出, 不消耗 attempt, 也不进入故障收尾
                    if (_disposed || ctx.Token.IsCancellationRequested) return;
                    attempt++;
                    continue;
                }
            }

            // 稳定期内不读不写, 结束后才发送 Connected
            if (!await WaitForStabilizationAsync(ctx)) return;

            attempt = 0;
            StateChanged?.Invoke(portName, ConnectionState.Connected);

            // 返回 true 表示正常取消/释放, 不再重连; false 表示读会话异常结束, 计一次失败
            if (await RunReadSessionAsync(portName, ctx)) { break; }
            attempt++;
            StateChanged?.Invoke(portName, ConnectionState.Error);
        }

        FinalizePortState(portName, ctx, attempt);
    }

    private async Task<bool> ReadLoopAsync(string portName, PortContext ctx)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var protocolConfig = serviceProvider.GetRequiredService<IProtocolConfig>();
            CommunicationProtocol? lastProtocol = null;
            IProtocolParser? cachedParser = null;

            while (!ctx.Token.IsCancellationRequested)
            {
                int bytesRead = ReadFromPortSafe(ctx.Port, buffer, ctx.Token, out bool shouldExit);
                if (shouldExit) return false;
                if (bytesRead < 0) continue;

                // 协议切换不频繁, 按需解析一次
                var currentProtocol = protocolConfig.SelectedProtocol;
                if (currentProtocol != lastProtocol)
                {
                    lastProtocol = currentProtocol;
                    cachedParser = GetProtocolParser(currentProtocol);
                }

                if (cachedParser is not null)
                {
                    await DispatchDataToParserAsync(portName, cachedParser, buffer, bytesRead);
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] {Port} ReadLoop 未预期异常退出", portName);
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> HandleReconnectDelayAndOpenAsync(string portName, int baudRate, PortContext ctx, int attempt)
    {
        int delay = ReconnectBaseDelayMs * (1 << (attempt - 1));
        Log.Information("[SerialPort] {Port} 第 {Attempt}/{Max} 次重连, 等待 {Delay}ms 后重新打开串口",
            portName, attempt, MaxReconnectAttempts, delay);

        try
        {
            await Task.Delay(delay, ctx.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        CloseAndDisposePort(ctx.Port);

        var newPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = SerialPort.InfiniteTimeout,
            DtrEnable = false,
            RtsEnable = false
        };

        try
        {
            newPort.Open();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SerialPort] {Port} 重连打开串口失败, 继续重试", portName);
            CloseAndDisposePort(newPort);
            return false;
        }

        // 打开成功后再核对取消: 若等待/打开期间会话已被取代或用户断开,
        // 不能把刚打开的端口注册到已作废的上下文(否则该端口不会被任何会话释放)
        if (ctx.Token.IsCancellationRequested)
        {
            CloseAndDisposePort(newPort);
            return false;
        }

        ctx.Port = newPort;
        return true;
    }

    private static async Task<bool> WaitForStabilizationAsync(PortContext ctx)
    {
        try
        {
            await Task.Delay(StabilizationDelayMs, ctx.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> RunReadSessionAsync(string portName, PortContext ctx)
    {
        bool ok = await ReadLoopAsync(portName, ctx);
        // 如果是外部触发了 CancellationToken 取消或者宿主被 Dispose, 视作不需要重连
        if (_disposed || ctx.Token.IsCancellationRequested) return true;
        return ok;
    }

    private void FinalizePortState(string portName, PortContext ctx, int attempt)
    {
        if (attempt >= MaxReconnectAttempts)
        {
            Log.Warning("[SerialPort] {Port} 重连已达最大次数 {Max}, 放弃重连", portName, MaxReconnectAttempts);
            StateChanged?.Invoke(portName, ConnectionState.Error);
        }

        // 只拆除属于自己会话的注册项: 手动重连时旧任务可能晚于新注册到达收尾,
        // 字典里已是新连接的 ctx, 按名字无条件 TryRemove 会关闭刚打开的新串口
        if (_activePorts.TryGetValue(portName, out var current) && IsOwnedEntry(current, ctx))
        {
            if (_activePorts.TryRemove(portName, out var removed))
            {
                CleanupContext(removed);
            }
        }
    }

    /// <summary>收尾归属判定, 用引用相等确认字典当前注册项仍属于传入的会话上下文, current 为空视为不属于</summary>
    internal static bool IsOwnedEntry(PortContext? current, PortContext candidate)
        => ReferenceEquals(current, candidate);

    /// <summary>一次同步读取, 把三种结果写进返回值与 <paramref name="shouldExit"/></summary>
    /// <returns>实际字节数; -1 表示 ReadTimeout(正常轮询, 继续循环); 0 表示会话应结束</returns>
    /// <remarks>
    /// 超时在此转换为 -1, 让调用方用一条分支同时处理超时与读到数据。
    /// </remarks>
    private static int ReadFromPortSafe(SerialPort port, byte[] buffer, CancellationToken cancellationToken, out bool shouldExit)
    {
        shouldExit = false;
        try
        {
            int bytesRead = port.Read(buffer, 0, buffer.Length);
            // 串口上读到 0 字节是链路已不可用, 结束会话
            if (bytesRead == 0) shouldExit = true;
            return bytesRead;
        }
        catch (TimeoutException)
        {
            return -1;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            shouldExit = true;
            if (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Log.Warning(ex, "[SerialPort] {Port} 读取 I/O 错误", port.PortName);
            }
            return 0;
        }
    }

    private IProtocolParser? GetProtocolParser(CommunicationProtocol currentProtocol)
    {
        string? key = currentProtocol switch
        {
            CommunicationProtocol.JSON => "JSON",
            CommunicationProtocol.Modbus => "Modbus",
            _ => null
        };

        return key is not null ? serviceProvider.GetRequiredKeyedService<IProtocolParser>(key) : null;
    }

    /// <summary>把本次读到的字节复制一份交给解析器</summary>
    /// <remarks>
    /// 读缓冲来自 ArrayPool, 本次循环结束就会归还并可能被下一帧覆盖。
    /// </remarks>
    private static async Task DispatchDataToParserAsync(string portName, IProtocolParser parser, byte[] buffer, int bytesRead)
    {
        byte[] chunk = new byte[bytesRead];
        Array.Copy(buffer, chunk, bytesRead);

        try
        {
            await parser.OnDataReceivedAsync(portName, chunk);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] {Port} 转发数据到解析器失败", portName);
        }
    }

    /// <summary>写入一段原始字节, 与其它写操作在 <see cref="PortContext.WriteLock"/> 上串行</summary>
    /// <remarks>
    /// 端口未打开时静默返回, 断线瞬间的阈值下发属于可丢弃操作, 由调用方重连后重试。
    /// <paramref name="data"/> 只在本次调用内被读取, 返回后调用方可归还 ArrayPool。
    /// </remarks>
    public async Task WriteBytesAsync(string endpoint, byte[] data, int length)
    {
        if (!_activePorts.TryGetValue(endpoint, out var ctx) || !ctx.Port.IsOpen)
            return;

        await ctx.WriteLock.WaitAsync(ctx.Token);
        try
        {
            ctx.Port.Write(data, 0, length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] {Port} 写入失败 ({Len}B)", endpoint, length);
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }

    /// <summary>按 UTF-8 写入一行文本, 自动追加 <c>\n</c> 作为帧分隔符</summary>
    /// <remarks>行结束符是 JSON 行协议的成帧依据, 调用方不自行拼接</remarks>
    public async Task WriteStringAsync(string endpoint, string text)
    {
        if (!_activePorts.TryGetValue(endpoint, out var ctx) || !ctx.Port.IsOpen)
            return;

        byte[] data = Encoding.UTF8.GetBytes(text + "\n");

        await ctx.WriteLock.WaitAsync(ctx.Token);
        try
        {
            ctx.Port.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] {Port} 写入失败", endpoint);
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }

    /// <summary>关闭端口, 先清理注册项, 再取消读循环并释放句柄, 最后广播 Disconnected</summary>
    public void ClosePort(string portName)
    {
        if (_activePorts.TryRemove(portName, out var ctx))
        {
            CleanupContext(ctx);
            _portBaudRates.TryRemove(portName, out _);
            StateChanged?.Invoke(portName, ConnectionState.Disconnected);
            Log.Information("[SerialPort] {Port} 已关闭", portName);
        }
    }

    private static void CloseAndDisposePort(SerialPort? port)
    {
        if (port == null) return;
        try { if (port.IsOpen) port.Close(); } catch { /* 忽略异常 */ }
        try { port.Dispose(); } catch { /* 忽略异常 */ }
    }

    private static void CleanupContext(PortContext? ctx)
    {
        if (ctx == null) return;
        try { ctx.Cts.Cancel(); } catch { /* 忽略异常 */ }
        CloseAndDisposePort(ctx.Port);
        try { ctx.Dispose(); } catch { /* 忽略异常 */ }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            foreach (string portName in _activePorts.Keys.ToList())
            {
                ClosePort(portName);
            }
            _activePorts.Clear();
        }
        _disposed = true;
    }
}
