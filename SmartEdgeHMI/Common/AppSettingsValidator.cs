namespace SmartEdgeHMI.Common;

/// <summary>启动配置校验器: 检查用户设置文件中的关键参数是否在允许范围内</summary>
public static class AppSettingsValidator
{
    /// <summary>校验用户设置文件中的关键参数, 返回错误列表, 空列表表示通过</summary>
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        List<string> errors = [];

        if (settings.Connection.BaudRate is < 1200 or > 921600)
            errors.Add($"波特率(BaudRate)={settings.Connection.BaudRate}, 允许范围 1200~921600");

        if (settings.Modbus.SlaveAddress is < 1 or > 247)
            errors.Add($"Modbus 从站地址(SlaveAddress)={settings.Modbus.SlaveAddress}, 允许范围 1~247");

        if (settings.Modbus.PollingIntervalMs is < 50 or > 60000)
            errors.Add($"Modbus 轮询间隔毫秒(PollingIntervalMs)={settings.Modbus.PollingIntervalMs}, 允许范围 50~60000");

        if (settings.Watchdog.HeartbeatIntervalMs is < 500 or > 60000)
            errors.Add($"Watchdog 心跳间隔毫秒(HeartbeatIntervalMs)={settings.Watchdog.HeartbeatIntervalMs}, 允许范围 500~60000");

        if (settings.Watchdog.ConnectTimeoutMs is < 500 or > 60000)
            errors.Add($"Watchdog 连接超时毫秒(ConnectTimeoutMs)={settings.Watchdog.ConnectTimeoutMs}, 允许范围 500~60000");

        if (settings.UI.ChartRefreshRateMs is < 16 or > 1000)
            errors.Add($"图表刷新间隔毫秒(ChartRefreshRateMs)={settings.UI.ChartRefreshRateMs}, 允许范围 16~1000");

        if (settings.Hardware.DefaultThreshold is < -40 or > 125)
            errors.Add($"默认报警阈值(DefaultThreshold)={settings.Hardware.DefaultThreshold}, 允许范围 -40~125");

        if (settings.Alarm.RecoveryDebounceCount is < 1 or > 100)
            errors.Add($"报警恢复去抖次数(RecoveryDebounceCount)={settings.Alarm.RecoveryDebounceCount}, 允许范围 1~100");

        if (settings.Logging.MaxLogEntries is < 50 or > 100000)
            errors.Add($"日志最大条数(MaxLogEntries)={settings.Logging.MaxLogEntries}, 允许范围 50~100000");

        return errors;
    }
}
