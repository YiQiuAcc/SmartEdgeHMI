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
/// 串口物理层服务：只负责原始字节流的收发与转发，不关心数据格式。 内部采用双线程模型：
/// - ReadPortLoop：从 BaseStream 轮询读取原始字节，写入 Bounded Channel（生产者）
/// - ForwardDataLoop：从 Channel 读取字节块，直接 await 协议解析器（消费者） 两个线程之间通过 Channel 解耦，防止读线程被上层协议处理的耗时阻塞。
/// 消费者直接通过 DI 获取协议解析器并 await 异步调用，避免 sync-over-async。
/// </summary>
public class SerialPortService(IServiceProvider serviceProvider) : ISerialPortService, IDisposable
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ConcurrentDictionary<string, PortContext> _activePorts = new();

    private record PortContext(SerialPort Port, CancellationTokenSource Cts, Channel<byte[]> DataChannel);

    public string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    public void OpenPort(string portName, int baudRate)
    {
        var serialPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = AppConstants.SerialPortTimeoutMs,
            WriteTimeout = AppConstants.SerialPortTimeoutMs
        };

        var cts = new CancellationTokenSource();

        // Bounded Channel: 读线程写入 → 转发线程消费
        var channelOptions = new BoundedChannelOptions(AppConstants.SerialChannelCapacity)
        {
            SingleWriter = true,   // 仅 ReadPortLoop 一个生产者
            SingleReader = true    // 仅 ForwardDataLoop 一个消费者
        };
        var channel = Channel.CreateBounded<byte[]>(channelOptions);

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

        // 生产者线程：从串口 BaseStream 不断读取原始字节写入 Channel
        _ = Task.Run(async () =>
            {
                try
                {
                    await ReadPortLoopAsync(portName, context);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "串口读线程 ReadPortLoop({Port}) 发生致命异常并退出", portName);
                }
            }, cts.Token);

        // 消费者线程：从 Channel 读取字节块，直接 await 协议解析器
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
                catch (TimeoutException) { }
            }
        }
        catch (OperationCanceledException) { }
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
    /// 转发循环：从 Channel 读取原始字节，直接 await 协议解析器的异步方法。 避免了旧版通过 Messenger 同步广播导致的 sync-over-async 问题。
    /// </summary>
    private async Task ForwardDataLoopAsync(string portName, PortContext context)
    {
        // 预获取协议解析器引用
        var protocolConfig = _serviceProvider.GetRequiredService<IProtocolConfig>();
        var serviceProvider = _serviceProvider;

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
                    var parser = serviceProvider.GetRequiredKeyedService<IProtocolParser>(key);
                    // 真正的 await，不再阻塞线程池线程
                    await parser.OnDataReceivedAsync(portName, chunk.AsMemory());
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Forward] 协议解析器 {Key} 处理数据时异常", key);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void HandleUnexpectedDisconnect(string portName)
    {
        WeakReferenceMessenger.Default.Send(new DeviceStateChanged(portName, ConnectionState.Error, "硬件连接异常断开"));
        ClosePort(portName);
    }

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

    public async Task WriteStringAsync(string portName, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text + "\n");
        await WriteBytesAsync(portName, data, data.Length);
    }

    public void Dispose()
    {
        foreach (string portName in _activePorts.Keys.ToList())
        {
            ClosePort(portName);
        }
        GC.SuppressFinalize(this);
    }
}
