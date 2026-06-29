namespace SmartEdgeHMI.ViewModels;

public class MainViewModel
{
    public ConnectionViewModel Connection { get; }
    public MonitorViewModel Monitor { get; }
    public AlarmHistoryViewModel AlarmHistory { get; }
    public LogConsoleViewModel LogConsole { get; }
    public TrendViewModel Trend { get; }
    public ChartViewModel Chart { get; }

    public MainViewModel(
        ConnectionViewModel connection,
        MonitorViewModel monitor,
        AlarmHistoryViewModel alarmHistory,
        LogConsoleViewModel logConsole,
        TrendViewModel trend,
        ChartViewModel chart)
    {
        Connection = connection;
        Monitor = monitor;
        AlarmHistory = alarmHistory;
        LogConsole = logConsole;
        Trend = trend;
        Chart = chart;

        Trend.HistoryDataLoaded += data => Chart.LoadHistory(data);
        Trend.LiveModeRestored += () => Chart.ClearToLiveMode();
    }
}
