using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Communication.Ports;

/// <summary>
/// 串口物理层服务:只负责原始字节流的收发与转发, 不关心数据格式。 内部采用双线程模型:
/// - ReadPortLoop:从 BaseStream 轮询读取原始字节, 写入 Bounded Channel(生产者)
/// - ForwardDataLoop:从 Channel 读取字节块, 通过 Messenger 广播给协议层(消费者) 两个线程之间通过 Channel 解耦,
/// 防止读线程被上层协议处理的耗时阻塞。
/// </summary>
public class SerialPortService : ISerialPortService, IDisposable
{
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

        // Bounded Channel:读线程写入 → 转发线程消费, 容量 1000 个数据块
        var channelOptions = new BoundedChannelOptions(1000)
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
            return; // 端口已存在, 不重复创建
        }

        WeakReferenceMessenger.Default.Send(new DeviceStateChanged(portName, ConnectionState.Connected));

        // 生产者线程:从串口 BaseStream 不断读取原始字节写入 Channel
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

        // 消费者线程:从 Channel 读取字节块, 通过 Messenger 广播给协议层
        Task.Run(() => ForwardDataLoop(portName, context))
            .ContinueWith(t => Log.Error(t.Exception, "串口转发线程 ForwardDataLoop({Port}) 异常退出", portName),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 串口读取循环:从 BaseStream.ReadAsync 轮询读取原始字节块, 通过 Channel.TryWrite 零阻塞地推入转发队列。
    /// 使用 ArrayPool 复用 4KB 缓冲区, 避免高频分配。
    /// </summary>
    private async Task ReadPortLoopAsync(string portName, PortContext context)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (!context.Cts.Token.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await context.Port.BaseStream.ReadAsync(buffer, context.Cts.Token);
                    if (bytesRead > 0)
                    {
                        // 从池中复制出独立的数据块, 归还缓冲区供下次复用
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
            context.DataChannel.Writer.Complete(); // 通知转发线程不再有新数据
        }
    }

    private static async Task ForwardDataLoop(string portName, PortContext context)
    {
        try
        {
            await foreach (byte[] chunk in context.DataChannel.Reader.ReadAllAsync(context.Cts.Token))
            {
                WeakReferenceMessenger.Default.Send(new RawDataReceived(portName, chunk));
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>处理意外断线:广播错误状态并清理端口资源</summary>
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
