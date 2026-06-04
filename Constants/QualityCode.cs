namespace SmartEdgeHMI.Constants;

/// <summary>
/// 设备数据质量码
/// </summary>
public enum QualityCode
{
    Good = 0,
    Bad = 1,
    Uncertain = 2,
    BadDeviceOffline = 3,
    BadDeviceNotConnected = 4,
    BadDeviceNotAuthorized = 5,
    BadDeviceNotSupported = 6,
    BadDeviceNotConfigured = 7,
    BadDeviceNotInitialized = 8,
    BadDeviceNotReady = 9,
}
