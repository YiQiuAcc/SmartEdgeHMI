namespace SmartEdgeHMI.ViewModels;

/// <summary>主窗口视图模型: 只做子视图模型的组装与跨页连接</summary>
/// <remarks>
/// 趋势页与图表页之间的联动在这里接线, 保持子页之间互不依赖。
/// </remarks>
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

        // 历史回放与实时模式的切换由趋势页发起, 图表页被动接收
        Trend.HistoryDataLoaded += data => Chart.LoadHistory(data);
        Trend.LiveModeRestored += () => Chart.ClearToLiveMode();
    }
}
