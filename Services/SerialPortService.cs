using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

public class SerialPortService : ISerialPortService
{
    // 将 CTS、端口实例和处理数据的 Channel 绑定在一起
    private readonly ConcurrentDictionary<string, PortContext> _activePorts = new();

    private record PortContext(SerialPort Port, CancellationTokenSource Cts, Channel<string> DataChannel);

    public string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    public void OpenPort(string portName, int baudRate = 115200)
    {
        if (_activePorts.ContainsKey(portName)) return;

        var serialPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            // 如果单片机是用 println() 发送, 默认就是 "\r\n" 或者 "\n"
        };

        var cts = new CancellationTokenSource();

        // 创建一个有界通道, 防止内存爆满
        var channelOptions = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 缓冲区满时丢弃旧数据, 保证数据实时性
            SingleWriter = true,
            SingleReader = true
        };
        var channel = Channel.CreateBounded<string>(channelOptions);

        serialPort.Open();
        var context = new PortContext(serialPort, cts, channel);

        if (_activePorts.TryAdd(portName, context))
        {
            // 启动生产者线程 (读取串口底层缓冲)
            Task.Factory.StartNew(() => ReadPortLoop(portName, context),
                cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // 启动消费者线程 (处理业务逻辑和消息分发)
            Task.Run(() => ConsumeDataLoop(portName, context));
        }
    }

    private void ReadPortLoop(string portName, PortContext context)
    {
        try
        {
            while (!context.Cts.Token.IsCancellationRequested)
            {
                try
                {
                    var line = context.Port.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        // 写入 Channel, 非阻塞。写满了会自动覆盖旧数据
                        context.DataChannel.Writer.TryWrite(line);
                    }
                }
                catch (TimeoutException) { /* 正常超时, 继续循环等待新数据 */ }
            }
        }
        catch (IOException ex)
        {
            Log.Error(ex, "物理断线或 I/O 错误: {PortName}", portName);
            HandleUnexpectedDisconnect(portName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取任务发生未知异常 {PortName}", portName);
            HandleUnexpectedDisconnect(portName);
        }
        finally
        {
            context.DataChannel.Writer.Complete(); // 通知消费者不再有新数据
        }
    }

    private async Task ConsumeDataLoop(string portName, PortContext context)
    {
        try
        {
            // 异步读取 Channel 中的数据, 彻底解放读取线程
            await foreach (var line in context.DataChannel.Reader.ReadAllAsync(context.Cts.Token))
            {
                try
                {
                    // JSON 反序列化为 DTO
                    var payload = JsonSerializer.Deserialize<TelemetryPayload>(line);

                    if (payload != null)
                    {
                        // 成功解析后, 发送强类型的 Message
                        WeakReferenceMessenger.Default.Send(new TelemetryReceivedMessage(portName, payload));
                    }
                }
                catch (JsonException ex)
                {
                    // 偶尔出现的乱码（粘包、串口波特率抖动）不应该让程序崩溃, 记录日志即可
                    Log.Warning(ex, "串口 {PortName} 收到无效的 JSON 数据: {RawData}", portName, line);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常取消, 退出循环 */ }
    }

    private void HandleUnexpectedDisconnect(string portName)
    {
        ClosePort(portName);
        // 发送规范的设备状态变更消息, 通知 ViewModel 做出界面反馈
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

    // 使用 BaseStream 异步写入
    public async Task SendCommandAsync(string portName, CommandPayload commandPayload)
    {
        if (_activePorts.TryGetValue(portName, out var context) && context.Port.IsOpen)
        {
            try
            {
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(commandPayload);
                // 追加特定的协议结束符 "\n" 帮助单片机截断
                var packet = new byte[jsonBytes.Length + 1];
                jsonBytes.CopyTo(packet, 0);
                packet[^1] = (byte)'\n';
                // 使用 BaseStream 进行非阻塞异步写入
                await context.Port.BaseStream.WriteAsync(packet, 0, packet.Length, context.Cts.Token);
                await context.Port.BaseStream.FlushAsync(context.Cts.Token);
                Log.Information("指令已发送至 {PortName}", portName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "向 {PortName} 发送指令失败", portName);
            }
        }
    }
}
