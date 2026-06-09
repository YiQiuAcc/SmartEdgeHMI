using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

/// <summary>协议层：消费物理层原始字节流，按 JSON-Lines 协议解析为强类型遥测消息</summary>
public class JsonProtocolService : IRecipient<RawDataReceivedMessage>, IRecipient<DeviceStateChangedMessage>
{
    private readonly ConcurrentDictionary<string, List<byte>> _lineBuffers = new();

    public JsonProtocolService()
    {
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public void Receive(RawDataReceivedMessage message)
    {
        if (message.Data.Length == 0) return;

        var buffer = _lineBuffers.GetOrAdd(message.PortName, _ => []);
        buffer.AddRange(message.Data);

        // 按 \n 切分 JSON 行
        int consumed = 0;
        while (consumed < buffer.Count)
        {
            int newlineIdx = buffer.IndexOf((byte)'\n', consumed);
            if (newlineIdx < 0) break; // 没有完整行, 等待更多数据

            int lineLength = newlineIdx - consumed;
            if (lineLength > 0)
            {
                ProcessLine(message.PortName, buffer.GetRange(consumed, lineLength));
            }

            consumed = newlineIdx + 1; // 跳过 \n
        }

        // 移除已消费的字节, 保留未完成的行
        if (consumed > 0)
            buffer.RemoveRange(0, consumed);
    }

    private static void ProcessLine(string portName, List<byte> lineBytes)
    {
        try
        {
            string json = Encoding.UTF8.GetString(lineBytes.ToArray());
            var payload = JsonSerializer.Deserialize<TelemetryPayload>(json);
            if (payload != null)
                WeakReferenceMessenger.Default.Send(new TelemetryReceivedMessage(portName, payload));
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "串口 {PortName} 收到无效的 JSON 数据: {RawData}",
                portName, Encoding.UTF8.GetString(lineBytes.ToArray()));
        }
    }

    public void Receive(DeviceStateChangedMessage message)
    {
        if (message.State == ConnectionState.Disconnected || message.State == ConnectionState.Error)
            _lineBuffers.TryRemove(message.PortName, out _);
    }
}
