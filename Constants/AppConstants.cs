namespace SmartEdgeHMI.Constants;

public static class AppConstants
{
    // --- 串口通信标准配置 ---
    public static readonly string[] StandardBaudRates =
    {
        "4800", "9600", "19200", "38400", "57600", "115200"
    };

    // --- 系统时间与阈值参数 ---
    /// <summary>串口读写默认超时时间 (毫秒)</summary>
    public const int SerialPortTimeoutMs = 500;

    /// <summary>配置文件防抖保存的延迟时间 (毫秒)</summary>
    public const int SettingsSaveDebounceMs = 3000;

    /// <summary>报警恢复需连续正常的帧数，防止阈值边界震荡导致报警风暴</summary>
    public const int AlarmRecoveryDebounceCount = 3;

    /// <summary>UI 内存中保留的最大日志条数，防止内存溢出 (OOM)</summary>
    public const int MaxLogEntries = 500;

    // --- 默认硬件配置 ---
    public const string DefaultDeviceName = "Sensor_01";
}
