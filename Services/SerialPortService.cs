using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

/// <summary>物理层：只管原始字节的收发，不关心数据格式</summary>
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

        var channelOptions = new BoundedChannelOptions(1000)
        {
            SingleWriter = true,
            SingleReader = true
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

        // 物理层读线程：用 ReadLine() 可靠读取，转字节写入 Channel
        _ = Task.Run(async () =>
            {
                try
                {
                    // await 会确保真正等待这个异步循环彻底结束
                    await ReadPortLoopAsync(portName, context);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "ReadPortLoop({Port}) 发生致命异常并退出", portName);
                }
            }, cts.Token);
        // 转发线程：将字节块通过 Messenger 发给协议层
        Task.Run(() => ForwardDataLoop(portName, context))
            .ContinueWith(t => Log.Error(t.Exception, "ForwardDataLoop({Port}) 异常退出", portName),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task ReadPortLoopAsync(string portName, PortContext context)
    {
        // 从内存池借用一个 4KB 用来接字节流
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
                        // 把读到的数据拷贝出来
                        byte[] chunk = new byte[bytesRead];
                        Array.Copy(buffer, chunk, bytesRead);
                        // 扔进 Channel, 向上层广播
                        context.DataChannel.Writer.TryWrite(chunk);
                    }
                }
                catch (TimeoutException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!context.Cts.Token.IsCancellationRequested)
        {
            Log.Error(ex, "读取任务发生异常: {PortName}", portName);
            HandleUnexpectedDisconnect(portName);
        }
        finally
        {
            // 用完桶还给池子
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            context.DataChannel.Writer.Complete();
        }
    }

    private static async Task ForwardDataLoop(string portName, PortContext context)
    {
        try
        {
            await foreach (byte[] chunk in context.DataChannel.Reader.ReadAllAsync(context.Cts.Token))
            {
                WeakReferenceMessenger.Default.Send(new RawDataReceivedMessage(portName, chunk));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void HandleUnexpectedDisconnect(string portName)
    {
        ClosePort(portName);
        WeakReferenceMessenger.Default.Send(new DeviceStateChangedMessage(portName, ConnectionState.Disconnected, "Unexpected hardware disconnection"));
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
        }
    }

    public async Task WriteBytesAsync(string portName, byte[] data, int length)
    {
        if (_activePorts.TryGetValue(portName, out var context) && context.Port.IsOpen)
        {
            try
            {
                await context.Port.BaseStream.WriteAsync(data, 0, length, context.Cts.Token);
                await context.Port.BaseStream.FlushAsync(context.Cts.Token);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "向 {PortName} 写入字节流失败", portName);
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
