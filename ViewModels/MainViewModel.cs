namespace SmartEdgeHMI.ViewModels;

public partial class MainViewModel(
    ConnectionViewModel connection,
    MonitorViewModel monitor,
    AlarmHistoryViewModel alarmHistory,
    LogConsoleViewModel logConsole,
    TrendViewModel trend)
{
    public ConnectionViewModel Connection { get; } = connection;
    public MonitorViewModel Monitor { get; } = monitor;
    public AlarmHistoryViewModel AlarmHistory { get; } = alarmHistory;
    public LogConsoleViewModel LogConsole { get; } = logConsole;
    public TrendViewModel Trend { get; } = trend;
}
