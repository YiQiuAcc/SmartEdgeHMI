namespace SmartEdgeHMI.Constants;

/// <summary>
/// 下游设备状态
/// </summary>
public enum Status
{
    Online = 0,
    Offline = 1,
    Error = 2,
    Disabled = 3,
    Stopped = 4,
    NotReady = 5,
    NotInitialized = 6,
    NotConfigured = 7,
    NotSupported = 8,
    NotConnected = 9,
    NotAuthorized = 10,
    NotEnabled = 11,
    NotDisabled = 12,
}
