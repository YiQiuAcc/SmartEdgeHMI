using CommunityToolkit.Mvvm.Messaging.Messages;
using SmartEdgeHMI.Models.DTOs;

namespace SmartEdgeHMI.Models.Messages;

/// <summary>串口服务成功解析出合法 JSON 遥测数据后, 发出的强类型事件</summary>
public class TelemetryReceivedMessage(string portName, TelemetryPayload payload)
    : ValueChangedMessage<TelemetryPayload>(payload)
{
    public string PortName { get; } = portName;

    public double Temperature => Value.Temperature;
}
