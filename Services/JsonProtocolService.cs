using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Infrastructure;
using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

/// <summary>协议层：消费物理层原始字节流, 按 JSON-Lines 协议解析为强类型遥测消息</summary>
public class JsonProtocolService : IRecipient<RawDataReceivedMessage>, IRecipient<DeviceStateChangedMessage>, IDisposable
{
    private readonly ConcurrentDictionary<string, SlidingBuffer> _lineBuffers = new();

    public JsonProtocolService()
    {
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public void Receive(RawDataReceivedMessage message)
    {
        if (message.Data.Length == 0) return;

        var buffer = _lineBuffers.GetOrAdd(message.PortName, _ => new SlidingBuffer());

        // 保证线程安全, 防止多个接收事件交错污染缓冲区
        lock (buffer)
        {
            buffer.Append(message.Data);
            ProcessBuffer(message.PortName, buffer);
        }
    }

    private static void ProcessBuffer(string portName, SlidingBuffer buffer)
    {
        while (buffer.Length > 0)
        {
            ReadOnlySpan<byte> unreadSpan = buffer.UnreadSpan;

            // 查找下一个换行符 \n (0x0A)
            int newlineIdx = unreadSpan.IndexOf((byte)'\n');
            if (newlineIdx < 0) break; // 没有找到换行符, 说明是半行数据, 等下一波断包拼凑

            // 提取这一整行的 Span
            ReadOnlySpan<byte> lineSpan = unreadSpan[..newlineIdx];

            // 兼容 CRLF (\r\n) 和 LF (\n) 格式：如果末尾是 \r (0x0D), 切掉它
            if (lineSpan.Length > 0 && lineSpan[^1] == (byte)'\r') lineSpan = lineSpan[..^1];

            // 过滤空行
            if (lineSpan.Length > 0) ProcessLine(portName, lineSpan);

            // 消费掉当前行以及末尾的 \n
            buffer.Consume(newlineIdx + 1);
        }
    }

    private static void ProcessLine(string portName, ReadOnlySpan<byte> lineSpan)
    {
        try
        {
            // 解析 UTF-8 字节流, 利用 Utf8JsonReader 直接穿透 Span 读取
            var payload = JsonSerializer.Deserialize<TelemetryPayload>(lineSpan);

            if (payload != null)
            {
                WeakReferenceMessenger.Default.Send(new DeviceTelemetryMessage(portName, payload));
            }
        }
        catch (JsonException ex)
        {
            string badJson = Encoding.UTF8.GetString(lineSpan);
            Log.Warning(ex, "串口 {PortName} 收到无效的 JSON 数据: {RawData}", portName, badJson);
        }
    }

    public void Receive(DeviceStateChangedMessage message)
    {
        if (message.State == ConnectionState.Disconnected || message.State == ConnectionState.Error)
        {
            if (_lineBuffers.TryRemove(message.PortName, out var buffer))
            {
                buffer.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var buffer in _lineBuffers.Values)
        {
            buffer.Dispose();
        }
        _lineBuffers.Clear();
        GC.SuppressFinalize(this);
    }
}
