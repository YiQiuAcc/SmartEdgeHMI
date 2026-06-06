using System.Text.Json;
using SmartEdgeHMI.Constants;

namespace SmartEdgeHMI.Models;

/// <summary>下发指令</summary>
public class DeviceCommand
{
    public Guid CommandId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public DeviceAction Action { get; set; }
    public JsonElement? Payload { get; set; }
}
