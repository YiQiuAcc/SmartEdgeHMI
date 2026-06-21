namespace SmartEdgeHMI.Models.Messages;

public enum ConnectionState
{ Connected, Disconnected, Error }

/// <summary>设备物理状态变更通知</summary>
public class DeviceStateChanged(string portName, ConnectionState state, string? errorDetails = null)
{
    public string PortName { get; } = portName;
    public ConnectionState State { get; } = state;
    public string? ErrorDetails { get; } = errorDetails;
}
