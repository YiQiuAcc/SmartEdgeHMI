using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using ScottPlot.TickGenerators;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views;

public partial class MainWindow : Window,
    IRecipient<DeviceTelemetryMessage>,
    IRecipient<SensorReadingMessage>,
    IRecipient<TrendDataLoadedMessage>
{
    private readonly Dictionary<string, ScottPlot.Plottables.DataLogger> _loggers = [];
    private static readonly Color[] _loggerColors = [Colors.Cyan, Colors.Gold];

    private const int DataLoggerCapacity = 2000;      // 期望保留的核心基准点数
    private const int DataLoggerMaxThreshold = 2500;  // 允许增长到的最大上限

    private readonly DispatcherTimer _renderTimer;
    private bool _isHistoryMode;

    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        InitializePlot();

        DataContext = serviceProvider.GetRequiredService<MainViewModel>();
        WeakReferenceMessenger.Default.RegisterAll(this);

        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += (s, e) => DataPlot.Refresh();
        _renderTimer.Start();
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

    public void Receive(DeviceTelemetryMessage message)
        => AddTemperature(message.PortName, message.Payload.Temperature);

    public void Receive(SensorReadingMessage message)
        => AddTemperature(message.PortName, message.Temperature);

    public void Receive(TrendDataLoadedMessage message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            if (message.Data.Count == 0)
            {
                _isHistoryMode = false;
                DataPlot.Plot.Clear();
                _loggers.Clear();
                DataPlot.Plot.Axes.Bottom.TickGenerator = new DateTimeAutomatic();
                DataPlot.Plot.Axes.AutoScale();
                DataPlot.Refresh();
                return;
            }

            _isHistoryMode = true;
            DataPlot.Plot.Clear();
            _loggers.Clear();

            var xs = message.Data.Select(d => d.Timestamp.ToOADate()).ToArray();
            var ys = message.Data.Select(d => d.Temperature).ToArray();
            var scatter = DataPlot.Plot.Add.Scatter(xs, ys);
            scatter.LineWidth = 2;
            scatter.Color = Colors.Cyan;

            DataPlot.Plot.Axes.Bottom.TickGenerator = new DateTimeAutomatic();
            DataPlot.Plot.Axes.AutoScale();
            DataPlot.Refresh();
        });
    }

    private void AddTemperature(string portName, double temperature)
    {
        if (_isHistoryMode) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            if (!_loggers.TryGetValue(portName, out var logger))
            {
                logger = DataPlot.Plot.Add.DataLogger();
                logger.LineWidth = 2;
                logger.Color = _loggerColors[_loggers.Count % _loggerColors.Length];
                _loggers[portName] = logger;
                DataPlot.Plot.Axes.AutoScale();
            }

            logger.Add(DateTime.Now.ToOADate(), temperature);

            // 当点数顶到 2500 的极大上限时, 触发一次批量裁剪
            if (logger.Data.Coordinates.Count > DataLoggerMaxThreshold)
            {
                // 切掉最旧的 500 个点, 拿最新的 2000 个点
                var recent = logger.Data.Coordinates
                    .Skip(logger.Data.Coordinates.Count - DataLoggerCapacity)
                    .ToList();

                logger.Clear();
                foreach (var pt in recent)
                {
                    logger.Add(pt);
                }
            }
        });
    }
}
