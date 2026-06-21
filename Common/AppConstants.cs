namespace SmartEdgeHMI.Common;

public static class AppConstants
{
    // 串口通信配置
    public static readonly string[] StandardBaudRates =
    [
        "4800", "9600", "19200", "38400", "57600", "115200"
    ];

    // 系统时间与阈值参数
    public const int SerialPortTimeoutMs = 500;
    public const int SettingsSaveDebounceMs = 3000;
    public const int AlarmRecoveryDebounceCount = 3;
    public const int MaxLogEntries = 500;

    // 默认硬件配置
    public const string DefaultDeviceName = "Sensor_01";
    public const byte DefaultModbusSlaveAddress = 1;
}
