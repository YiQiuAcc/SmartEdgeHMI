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

namespace SmartEdgeHMI.Protocols.Transports.Serial;

/// <summary>
/// 高性能全双工异步模型串口物理层服务
/// - ReadLoop: 基于底层的同步 Read 配合 500ms 超时轮询，绕过 Win32 异步重叠 I/O 冲突 Bug
/// - WriteLock: 仅用于保护发送端，防止多线程并发写入时字节流混杂
/// </summary>
public class SerialPortService(IServiceProvider serviceProvider) : ISerialPortService, IDisposable
{
    private readonly ConcurrentDictionary<string, PortContext> _activePorts = new();
    private readonly ConcurrentDictionary<string, int> _portBaudRates = new();
    private bool _disposed;

    private const int StabilizationDelayMs = 5000;
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectBaseDelayMs = 5000;

    public event Action<string, ConnectionState>? StateChanged;

    private sealed class PortContext(SerialPort port, CancellationTokenSource cts) : IDisposable
    {
        public SerialPort Port { get; set; } = port;
        public CancellationTokenSource Cts { get; set; } = cts;
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
            ReadTimeout = 500, // 同步读超时
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

        _ = Task.Run(() => RunReadLoopWithReconnectAsync(portName, baudRate, context), cts.Token);
    }

    private async Task RunReadLoopWithReconnectAsync(string portName, int baudRate, PortContext ctx)
    {
        int attempt = 0;

        while (!_disposed && attempt < MaxReconnectAttempts)
        {
            if (attempt > 0 && !await HandleReconnectDelayAndOpenAsync(portName, baudRate, ctx, attempt))
            {
                attempt++;
                continue;
            }

            // 等待串口稳定
            if (!await WaitForStabilizationAsync(ctx)) return;

            attempt = 0; // 度过稳定期后重置计数器
            StateChanged?.Invoke(portName, ConnectionState.Connected);

            // 执行读取会话, 若因正常取消或释放则直接退出循环
            if (await RunReadSessionAsync(portName, ctx)) { break; }
            attempt++;
            StateChanged?.Invoke(portName, ConnectionState.Error);
        }

        FinalizePortState(portName, attempt);
    }

    private async Task<bool> ReadLoopAsync(string portName, PortContext ctx)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var protocolConfig = serviceProvider.GetRequiredService<IProtocolConfig>();
            CommunicationProtocol? lastProtocol = null;
            IProtocolParser? cachedParser = null;

            while (!ctx.Cts.Token.IsCancellationRequested)
            {
                // 读取底层数据
                int bytesRead = ReadFromPortSafe(ctx.Port, buffer, ctx.Cts.Token, out bool shouldExit);
                if (shouldExit) return false;
                if (bytesRead < 0) continue;
                // 降低 DI 容器检索频率, 仅在协议切换时更新 Parser
                var currentProtocol = protocolConfig.SelectedProtocol;
                if (currentProtocol != lastProtocol)
                {
                    lastProtocol = currentProtocol;
                    cachedParser = GetProtocolParser(currentProtocol);
                }
                // 将数据分发给外部解析器
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
            await Task.Delay(delay, ctx.Cts.Token);
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
            ctx.Port = newPort;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SerialPort] {Port} 重连打开串口失败, 继续重试", portName);
            CloseAndDisposePort(newPort);
            return false;
        }
    }

    private static async Task<bool> WaitForStabilizationAsync(PortContext ctx)
    {
        try
        {
            await Task.Delay(StabilizationDelayMs, ctx.Cts.Token);
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
        // 如果是外部触发了 CancellationToken 取消或者宿主被 Dispose 了解构, 应视作“不需要再重连”, 直接返回 true
        if (_disposed || ctx.Cts.Token.IsCancellationRequested) return true;
        return ok;
    }

    private void FinalizePortState(string portName, int attempt)
    {
        if (attempt >= MaxReconnectAttempts)
        {
            Log.Warning("[SerialPort] {Port} 重连已达最大次数 {Max}, 放弃重连", portName, MaxReconnectAttempts);
            StateChanged?.Invoke(portName, ConnectionState.Error);
        }

        if (_activePorts.TryRemove(portName, out var removed))
        {
            CleanupContext(removed);
        }
    }

    private static int ReadFromPortSafe(SerialPort port, byte[] buffer, CancellationToken cancellationToken, out bool shouldExit)
    {
        shouldExit = false;
        try
        {
            int bytesRead = port.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) shouldExit = true;
            return bytesRead;
        }
        catch (TimeoutException)
        {
            return -1; // 返回负数代表触发了正常的超时轮询
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

    public async Task WriteBytesAsync(string endpoint, byte[] data, int length)
    {
        if (!_activePorts.TryGetValue(endpoint, out var ctx) || !ctx.Port.IsOpen)
            return;

        await ctx.WriteLock.WaitAsync(ctx.Cts.Token);
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

    public async Task WriteStringAsync(string endpoint, string text)
    {
        if (!_activePorts.TryGetValue(endpoint, out var ctx) || !ctx.Port.IsOpen)
            return;

        byte[] data = Encoding.UTF8.GetBytes(text + "\n");

        await ctx.WriteLock.WaitAsync(ctx.Cts.Token);
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
        try { if (port.IsOpen) port.Close(); } catch { /* best-effort */ }
        try { port.Dispose(); } catch { /* ignore */ }
    }

    private static void CleanupContext(PortContext? ctx)
    {
        if (ctx == null) return;
        try { ctx.Cts.Cancel(); } catch { /* ignore */ }
        CloseAndDisposePort(ctx.Port);
        try { ctx.Dispose(); } catch { /* ignore */ }
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
