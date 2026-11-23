namespace SmartEdgeHMI.Common;

/// <summary>串口连接配置, 持久化到 settings.json</summary>
public class ConnectionSettings
{
    public string ComPort { get; set; } = string.Empty;

    public int BaudRate { get; set; } = 115200;

    public string Protocol { get; set; } = "JSON";
}

/// <summary>Modbus 采集参数</summary>
public class ModbusSettings
{
    /// <summary>从站地址, 有效范围 1~247(0 为广播, 248~255 保留)</summary>
    public byte SlaveAddress { get; set; } = 1;

    /// <summary>轮询周期, 单位 ms; 下限受串口往返时延约束, 由 AppSettingsValidator 兜底校验</summary>
    public int PollingIntervalMs { get; set; } = 1000;
}

/// <summary>Watchdog 心跳参数</summary>
public class WatchdogSettings
{
    /// <summary>心跳发送周期, 单位 ms</summary>
    public int HeartbeatIntervalMs { get; set; } = 3000;

    /// <summary>管道断开后的重连间隔, 单位 ms</summary>
    public int ReconnectDelayMs { get; set; } = 2000;

    /// <summary>等待 Watchdog 管道就绪的超时, 单位 ms</summary>
    public int ConnectTimeoutMs { get; set; } = 5000;
}

/// <summary>界面刷新参数</summary>
public class UISettings
{
    /// <summary>图表刷新周期, 单位 ms; 33 约等于 30 FPS</summary>
    public int ChartRefreshRateMs { get; set; } = 33;
}

/// <summary>硬件相关参数</summary>
public class HardwareSettings
{
    /// <summary>默认报警阈值, 单位 °C; 连接建立后自动下发到设备</summary>
    public double DefaultThreshold { get; set; } = 100.0;
}

/// <summary>报警判定参数</summary>
public class AlarmSettings
{
    /// <summary>连续多少个正常读数才判定恢复, 用于滤掉单次抖动</summary>
    public int RecoveryDebounceCount { get; set; } = 3;
}

/// <summary>日志参数</summary>
public class LoggingSettings
{
    /// <summary>界面日志控制台保留的最大条数, 超出后按 1/10 批量裁剪</summary>
    public int MaxLogEntries { get; set; } = 500;
}

/// <summary>应用配置根对象, 对应 %LOCALAPPDATA%\SmartEdgeHMI\settings.json</summary>
/// <remarks>
/// 文件缺失时从 appsettings.json 的对应节回填默认值, 因此新增字段须同时在这两处声明。
/// 取值范围由 <see cref="AppSettingsValidator"/> 统一校验, 此处默认值只需保证合法。
/// </remarks>
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
