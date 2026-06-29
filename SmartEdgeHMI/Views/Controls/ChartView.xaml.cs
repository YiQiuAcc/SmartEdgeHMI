using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.TickGenerators;
using SmartEdgeHMI.Database.Entities;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views.Controls;

public partial class ChartView : UserControl
{
    private readonly Dictionary<string, ScottPlot.Plottables.DataLogger> _loggers = [];
    private static readonly Color[] _loggerColors = [Colors.Cyan, Colors.Gold];

    private const int DataLoggerCapacity = 2000;
    private const int DataLoggerMaxThreshold = 2500;

    private readonly DispatcherTimer _renderTimer;
    private bool _isHistoryMode;
    private ChartViewModel? _viewModel;

    public ChartView()
    {
        InitializeComponent();
        InitializePlot();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => DataPlot.Refresh();
        _renderTimer.Start();

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

            double[] xs = data.Select(d => d.Timestamp.ToOADate()).ToArray();
            double[] ys = data.Select(d => d.Temperature.Celsius).ToArray();
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
