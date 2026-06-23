namespace SmartEdgeHMI.Common;

public static class AppConstants
{
    // ──────────────── 串口通信配置 ────────────────
    public static readonly string[] StandardBaudRates =
    [
        "4800", "9600", "19200", "38400", "57600", "115200"
    ];

    public const int SerialPortTimeoutMs = 500;
    public const int SerialReadBufferSize = 4096;
    public const int SerialChannelCapacity = 1000;

    // ──────────────── 系统时间与阈值参数 ────────────────
    public const int SettingsSaveDebounceMs = 3000;
    public const int AlarmRecoveryDebounceCount = 3;
    public const int MaxLogEntries = 500;

    // ──────────────── 默认硬件配置 ────────────────
    public const string DefaultDeviceName = "Sensor_01";
    public const byte DefaultModbusSlaveAddress = 1;

    // ──────────────── Modbus 通信参数 ────────────────
    public const int ModbusPollingIntervalMs = 1000;
    public const ushort ModbusPollStartAddress = 0;
    public const ushort ModbusPollRegisterCount = 4;
    public const int ModbusExceptionFrameLength = 5;
    public const int ModbusWriteFrameLength = 8;

    // ──────────────── IO 管道参数 ────────────────
    public const int PipeMinimumSegmentSize = 4096;

    // ──────────────── Watchdog ────────────────
    public const int WatchdogHeartbeatIntervalMs = 3000;
    public const int WatchdogReconnectDelayMs = 2000;
    public const int WatchdogConnectTimeoutMs = 5000;
}
