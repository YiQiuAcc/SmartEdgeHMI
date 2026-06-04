using System.Text.Json;
using SmartEdgeHMI.Constants;

namespace SmartEdgeHMI.Models;

/// <summary>
/// 下发指令
/// </summary>
public class DeviceCommand
{
    public required Guid CommandId { get; set; }
    public required string DeviceId { get; set; }
    public DateTime Timestamp { get; set; }
    public DeviceAction Action { get; set; }
    public JsonElement? Payload { get; set; }
}
