namespace SmartEdgeHMI.Models.Messages;

/// <summary>串口物理连接状态</summary>
public enum ConnectionState
{ Connected, Disconnected, Error }

/// <summary>设备物理状态变更通知, 由传输层在后台线程发出</summary>
public class DeviceStateChanged(string portName, ConnectionState state, string? errorDetails = null)
{
    public string PortName { get; } = portName;
    public ConnectionState State { get; } = state;

    /// <summary>仅 <see cref="ConnectionState.Error"/> 时可能带异常摘要, 用于界面提示</summary>
    public string? ErrorDetails { get; } = errorDetails;
}
