using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Communication.Protocols;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Communication.Ports;

/// <summary>
/// 串口物理层服务: 只负责原始字节流的收发与转发, 不关心数据格式。 内部采用双线程模型:
/// - ReadPortLoop: 从 BaseStream 轮询读取原始字节, 写入 Bounded Channel(生产者)
/// - ForwardDataLoop: 从 Channel 读取字节块, 直接 await 协议解析器(消费者) 两个线程之间通过 Channel 解耦, 防止读线程被上层协议处理的耗时阻塞。
/// 消费者直接通过 DI 获取协议解析器并 await 异步调用, 避免 sync-over-async。
/// </summary>
public class SerialPortService(IServiceProvider provider) : ISerialPortService, IDisposable
{
    private readonly IServiceProvider _serviceProvider = provider;
    private readonly ConcurrentDictionary<string, PortContext> _activePorts = new();
    private bool _disposed;

    private sealed record PortContext(SerialPort Port, CancellationTokenSource Cts, Channel<byte[]> DataChannel);

    public string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    /// <summary>打开指定串口并启动读写双线程</summary>
    public void OpenPort(string portName, int baudRate)
    {
        // 防御性编程：如果服务已销毁, 禁止操作
        ObjectDisposedException.ThrowIf(_disposed, this);

        var serialPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = AppConstants.SerialPortTimeoutMs,
            WriteTimeout = AppConstants.SerialPortTimeoutMs
        };

        var cts = new CancellationTokenSource();

        var channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(AppConstants.SerialChannelCapacity)
            {
                SingleWriter = true,
                SingleReader = true
            });

        serialPort.Open();
        var context = new PortContext(serialPort, cts, channel);

        if (!_activePorts.TryAdd(portName, context))
        {
            serialPort.Close();
            serialPort.Dispose();
            cts.Dispose();
            return;
        }

        WeakReferenceMessenger.Default.Send(new DeviceStateChanged(portName, ConnectionState.Connected));

        // 生产者: 从 BaseStream 轮询原始字节写入 Channel
        _ = Task.Run(async () =>
        {
            try
            {
                await ReadPortLoopAsync(portName, context);
            }
            catch (OperationCanceledException)
            {
                // 此处的取消属于预期内的正常退出
            }
            catch (Exception ex)
            {
                Log.Error(ex, "串口读线程 ReadPortLoop({Port}) 发生致命异常并退出", portName);
            }
        }, cts.Token);

        // 消费者: 从 Channel 读取字节块后 await 协议解析器
        Task.Run(() => ForwardDataLoopAsync(portName, context))
            .ContinueWith(t => Log.Error(t.Exception, "串口转发线程 ForwardDataLoop({Port}) 异常退出", portName),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task ReadPortLoopAsync(string portName, PortContext context)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(AppConstants.SerialReadBufferSize);
        try
        {
            while (!context.Cts.Token.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await context.Port.BaseStream.ReadAsync(buffer, context.Cts.Token);
                    if (bytesRead > 0)
                    {
                        byte[] chunk = new byte[bytesRead];
                        Array.Copy(buffer, chunk, bytesRead);
                        context.DataChannel.Writer.TryWrite(chunk);
                    }
                }
                catch (TimeoutException)
                {
                    // 轮询读超时属于正常现象
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 外部触发 Cancellation 时正常退出循环
        }
        catch (Exception ex) when (!context.Cts.Token.IsCancellationRequested)
        {
            Log.Error(ex, "串口读线程异常退出: {PortName}", portName);
            HandleUnexpectedDisconnect(portName);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            context.DataChannel.Writer.Complete();
        }
    }

    /// <summary>
    /// 转发循环: 从 Channel 读取原始字节, 直接 await 协议解析器的异步方法。 避免了旧版通过 Messenger 同步广播导致的 sync-over-async 问题。
    /// </summary>
    private async Task ForwardDataLoopAsync(string portName, PortContext context)
    {
        // 预获取协议解析器引用
        var protocolConfig = _serviceProvider.GetRequiredService<IProtocolConfig>();

        try
        {
            await foreach (byte[] chunk in context.DataChannel.Reader.ReadAllAsync(context.Cts.Token))
            {
                string? key = protocolConfig.SelectedProtocol switch
                {
                    CommunicationProtocol.JSON => "JSON",
                    CommunicationProtocol.Modbus => "Modbus",
                    _ => null
                };

                if (key is null) continue;

                try
                {
                    var parser = _serviceProvider.GetRequiredKeyedService<IProtocolParser>(key);
                    await parser.OnDataReceivedAsync(portName, chunk.AsMemory());
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Forward] 协议解析器 {Key} 处理数据时异常", key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 转发线程随 Channel 完成或被取消时正常退出
        }
    }

    private void HandleUnexpectedDisconnect(string portName)
    {
        WeakReferenceMessenger.Default.Send(new DeviceStateChanged(portName, ConnectionState.Error, "硬件连接异常断开"));
        ClosePort(portName);
    }

    /// <summary>关闭指定串口并清理线程和 Channel 资源</summary>
    public void ClosePort(string portName)
    {
        if (_activePorts.TryRemove(portName, out var context))
        {
            context.Cts.Cancel();
            try
            {
                if (context.Port.IsOpen) context.Port.Close();
            }
            catch (Exception ex) { Log.Warning(ex, "关闭串口时发生异常 {PortName}", portName); }
            finally
            {
                context.Port.Dispose();
                context.Cts.Dispose();
            }

            WeakReferenceMessenger.Default.Send(new DeviceStateChanged(portName, ConnectionState.Disconnected));
        }
    }

    /// <summary>异步向串口写入指定长度的字节数据</summary>
    public async Task WriteBytesAsync(string portName, byte[] data, int length)
    {
        if (_activePorts.TryGetValue(portName, out var context) && context.Port.IsOpen)
        {
            try
            {
                await context.Port.BaseStream.WriteAsync(data.AsMemory(0, length), context.Cts.Token);
                await context.Port.BaseStream.FlushAsync(context.Cts.Token);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "向串口 {PortName} 写入字节流失败", portName);
            }
        }
    }

    /// <summary>异步向串口写入文本(UTF-8 编码, 自动追加换行符)</summary>
    public async Task WriteStringAsync(string portName, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text + "\n");
        await WriteBytesAsync(portName, data, data.Length);
    }

    /// <summary>关闭所有活跃端口并释放资源</summary>
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
            // 释放托管资源：关闭并销毁所有活跃的端口
            foreach (string portName in _activePorts.Keys.ToList())
            {
                ClosePort(portName);
            }
            _activePorts.Clear();
        }

        _disposed = true;
    }
}
