namespace SmartEdgeHMI.Constants;

/// <summary>
/// 下游设备错误码
/// </summary>
public enum ErrorCode
{
    NoError = 0,
    DeviceOffline = 1,
    DeviceNotConnected = 2,
    DeviceNotAuthorized = 3,
    DeviceNotSupported = 4,
    DeviceNotConfigured = 5,
    DeviceNotInitialized = 6,
    DeviceNotReady = 7,
    DeviceNotEnabled = 8,
    DeviceNotDisabled = 9,
    DeviceNotStopped = 10,
}
