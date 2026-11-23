using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.TickGenerators;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views.Controls;

/// <summary>趋势图控件: ScottPlot 的宿主, 负责实时追加与历史渲染</summary>
/// <remarks>
/// ScottPlot 不是线程安全的, 所有绘图操作都经 Dispatcher 排队到 UI 线程执行;
/// 重绘不跟随数据点, 而由 33ms 的 DispatcherTimer 统一触发, 把渲染频率与采集频率解耦。
/// </remarks>
public partial class ChartView : UserControl
{
    private readonly Dictionary<string, ScottPlot.Plottables.DataLogger> _loggers = [];
    private static readonly Color[] _loggerColors = [Colors.Cyan, Colors.Gold];

    /// <summary>裁剪后保留的点数</summary>
    private const int DataLoggerCapacity = 2000;

    /// <summary>触发裁剪的阈值; 比容量高出一定余量, 避免每新增一个点都执行一次重建</summary>
    private const int DataLoggerMaxThreshold = 2500;

    private readonly DispatcherTimer _renderTimer;
    private bool _isHistoryMode;
    private ChartViewModel? _viewModel;

    public ChartView()
    {
        InitializeComponent();
        InitializePlot();

        // 固定 33ms 重绘(约 30 FPS): 高频采样下比每点一刷稳定
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => DataPlot.Refresh();
        _renderTimer.Start();

        // DataContext 会随导航切换, 因此在变化时先退订旧实例
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LiveDataPointAdded -= OnLivePoint;
            _viewModel.HistoryDataLoaded -= OnHistory;
            _viewModel.ChartCleared -= OnCleared;
        }

        _viewModel = e.NewValue as ChartViewModel;
        if (_viewModel is null) return;

        _viewModel.LiveDataPointAdded += OnLivePoint;
        _viewModel.HistoryDataLoaded += OnHistory;
        _viewModel.ChartCleared += OnCleared;
    }

    private void InitializePlot()
    {
        DataPlot.Plot.FigureBackground.Color = Color.FromHex("#1E1E1E");
        DataPlot.Plot.DataBackground.Color = Color.FromHex("#252526");
        DataPlot.Plot.Axes.Color(Colors.LightGray);
        DataPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 14;
        DataPlot.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
        DataPlot.Plot.Axes.Bottom.Label.FontSize = 16;
        DataPlot.Plot.Axes.Left.Label.FontSize = 16;
        DataPlot.Plot.Axes.Title.Label.FontSize = 20;
        DataPlot.Plot.Axes.Bottom.TickGenerator = new DateTimeAutomatic();
        DataPlot.Plot.Axes.AutoScale();
        DataPlot.Refresh();
    }

    private void OnLivePoint(DateTime time, double temperature)
    {
        // 历史回放期间丢弃实时点
        // _isHistoryMode 在 UI 线程写、此处(采集线程)读且未加屏障, 切换瞬间可能漏进一两个点
        if (_isHistoryMode) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            if (!_loggers.TryGetValue("default", out var logger))
            {
                logger = DataPlot.Plot.Add.DataLogger();
                logger.LineWidth = 2;
                logger.Color = _loggerColors[_loggers.Count % _loggerColors.Length];
                _loggers["default"] = logger;
                DataPlot.Plot.Axes.AutoScale();
            }

            logger.Add(time.ToOADate(), temperature);

            // 超过阈值才裁剪到容量, 逐点裁剪会让 DataLogger 每帧重建坐标数组
            if (logger.Data.Coordinates.Count > DataLoggerMaxThreshold)
            {
                var recent = logger.Data.Coordinates
                    .Skip(logger.Data.Coordinates.Count - DataLoggerCapacity)
                    .ToList();
                logger.Clear();
                foreach (var pt in recent)
                    logger.Add(pt);
            }
        });
    }

    private void OnHistory(IReadOnlyList<SensorReadingRecord> data)
    {
        _isHistoryMode = true;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            DataPlot.Plot.Clear();
            _loggers.Clear();

            double[] xs = [.. data.Select(d => d.Timestamp.ToOADate())];
            double[] ys = [.. data.Select(d => d.Temperature.Celsius)];
            var scatter = DataPlot.Plot.Add.Scatter(xs, ys);
            scatter.LineWidth = 2;
            scatter.Color = Colors.Cyan;

            DataPlot.Plot.Axes.Bottom.TickGenerator = new DateTimeAutomatic();
            DataPlot.Plot.Axes.AutoScale();
            DataPlot.Refresh();
        });
    }

    private void OnCleared()
    {
        _isHistoryMode = false;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            DataPlot.Plot.Clear();
            _loggers.Clear();
            DataPlot.Plot.Axes.Bottom.TickGenerator = new DateTimeAutomatic();
            DataPlot.Plot.Axes.AutoScale();
            DataPlot.Refresh();
        });
    }
}
