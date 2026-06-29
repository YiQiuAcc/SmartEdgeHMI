namespace SmartEdgeHMI.Common;

public class ConnectionSettings
{
    public string ComPort { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115200;
}

public class ModbusSettings
{
    public byte SlaveAddress { get; set; } = 1;
    public int PollingIntervalMs { get; set; } = 1000;
}

public class WatchdogSettings
{
    public int HeartbeatIntervalMs { get; set; } = 3000;
    public int ReconnectDelayMs { get; set; } = 2000;
    public int ConnectTimeoutMs { get; set; } = 5000;
}

public class UISettings
{
    public int ChartRefreshRateMs { get; set; } = 33;
}

public class HardwareSettings
{
    public double DefaultThreshold { get; set; } = 100.0;
}

public class AlarmSettings
{
    public int RecoveryDebounceCount { get; set; } = 3;
}

public class LoggingSettings
{
    public int MaxLogEntries { get; set; } = 500;
}

public class AppSettings
{
    public ConnectionSettings Connection { get; set; } = new();
    public ModbusSettings Modbus { get; set; } = new();
    public WatchdogSettings Watchdog { get; set; } = new();
    public UISettings UI { get; set; } = new();
    public HardwareSettings Hardware { get; set; } = new();
    public AlarmSettings Alarm { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}
