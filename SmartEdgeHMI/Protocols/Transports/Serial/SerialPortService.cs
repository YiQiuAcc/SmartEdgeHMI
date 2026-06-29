using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Protocols.Services;

namespace SmartEdgeHMI.Protocols.Ports;

/// <summary>
/// 高性能全双工异步模型串口物理层服务
/// - ReadLoop: 后台线程无锁、无超时挂起死等串口，读到原始流立刻无脑塞给上层 Pipeline 解析器。
/// - WriteLock: 仅用于保护发送端，防止多线程并发写入时字节流混杂。
/// </summary>
public class SerialPortService : ISerialPortService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, PortContext> _activePorts = new();
    private bool _disposed;

    public event Action<string, ConnectionState>? StateChanged;

    private sealed class PortContext
    {
        public SerialPort Port { get; }
        public CancellationTokenSource Cts { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public PortContext(SerialPort port, CancellationTokenSource cts)
        {
            Port = port;
            Cts = cts;
        }
    }

    public SerialPortService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    public void OpenPort(string portName, int baudRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Log.Information("[SerialPort] 打开串口 {Port}, 波特率 {Baud}", portName, baudRate);

        var serialPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout,
            DtrEnable = false,
            RtsEnable = false
        };

        var cts = new CancellationTokenSource();
        serialPort.Open();
        Log.Information("[SerialPort] {Port} 已打开", portName);

        var context = new PortContext(serialPort, cts);
        if (!_activePorts.TryAdd(portName, context))
        {
            serialPort.Close();
            serialPort.Dispose();
            cts.Dispose();
            return;
        }

        StateChanged?.Invoke(portName, ConnectionState.Connected);

        // 启动后台全双工独立读线程
        _ = Task.Run(() => ReadLoopAsync(portName, context), cts.Token);
        Log.Information("[SerialPort] {Port} 连接成功", portName);
    }

    /// <summary>无锁、无超时打断，全双工读取循环</summary>
    private async Task ReadLoopAsync(string portName, PortContext ctx)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var protocolConfig = _serviceProvider.GetRequiredService<IProtocolConfig>();

            while (!ctx.Cts.Token.IsCancellationRequested)
            {
                // 全双工、非阻塞的异步监听状态。
                int bytesRead = await ctx.Port.BaseStream.ReadAsync(buffer, ctx.Cts.Token);

                if (bytesRead == 0)
                {
                    // 读到 0 字节代表流已结束或物理断开
                    break;
                }

                // 准确提取读到的字节碎片
                byte[] chunk = new byte[bytesRead];
                Array.Copy(buffer, chunk, bytesRead);

                // 动态获取当前的协议解析器
                string? key = protocolConfig.SelectedProtocol switch
                {
                    CommunicationProtocol.JSON => "JSON",
                    CommunicationProtocol.Modbus => "Modbus",
                    _ => null
                };

                if (key is not null)
                {
                    try
                    {
                        var parser = _serviceProvider.GetRequiredKeyedService<IProtocolParser>(key);
                        await parser.OnDataReceivedAsync(portName, chunk);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[SerialPort] {Port} 转发数据到 {Key} 解析器失败", portName, key);
                    }
                }
            }
        }
        catch (OperationCanceledException) { } // 串口关闭引发的正常退出
        catch (IOException ex) when (!ctx.Cts.Token.IsCancellationRequested)
        {
            Log.Warning(ex, "[SerialPort] {Port} 读取 I/O 错误, 后台读循环退出", portName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] {Port} ReadLoop 异常退出", portName);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task WriteBytesAsync(string portName, byte[] data, int length)
    {
        if (!_activePorts.TryGetValue(portName, out var ctx) || !ctx.Port.IsOpen) return;

        // 使用 WriteLock 独立保护发送端
        await ctx.WriteLock.WaitAsync(ctx.Cts.Token);
        try
        {
            await ctx.Port.BaseStream.WriteAsync(data.AsMemory(0, length), ctx.Cts.Token);
            await ctx.Port.BaseStream.FlushAsync(ctx.Cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] {Port} 写入失败 ({Len}B)", portName, length);
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }

    public async Task WriteStringAsync(string portName, string text)
    {
        if (!_activePorts.TryGetValue(portName, out var ctx) || !ctx.Port.IsOpen) return;

        byte[] data = Encoding.UTF8.GetBytes(text + "\n");
        await ctx.WriteLock.WaitAsync(ctx.Cts.Token);
        try
        {
            await ctx.Port.BaseStream.WriteAsync(data, ctx.Cts.Token);
            await ctx.Port.BaseStream.FlushAsync(ctx.Cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SerialPort] {Port} 写入失败", portName);
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
            Log.Information("[SerialPort] {Port} 正在关闭...", portName);
            ctx.Cts.Cancel();
            try { if (ctx.Port.IsOpen) ctx.Port.Close(); }
            catch (Exception ex) { Log.Warning(ex, "[SerialPort] {Port} 关闭串口异常", portName); }
            finally
            {
                ctx.Port.Dispose();
                ctx.Cts.Dispose();
                ctx.WriteLock.Dispose();
            }
            StateChanged?.Invoke(portName, ConnectionState.Disconnected);
            Log.Information("[SerialPort] {Port} 已关闭", portName);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string portName in _activePorts.Keys.ToList())
            ClosePort(portName);
        _activePorts.Clear();
        GC.SuppressFinalize(this);
    }
}
