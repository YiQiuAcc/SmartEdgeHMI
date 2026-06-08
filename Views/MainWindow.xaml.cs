using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views;

public partial class MainWindow : Window, IRecipient<TelemetryReceivedMessage>
{
    private readonly Dictionary<string, ScottPlot.Plottables.DataStreamer> _streamers = [];
    private static readonly Color[] _streamerColors = [Colors.Cyan, Colors.Gold];

    // 增加一个渲染定时器
    private readonly DispatcherTimer _renderTimer;

    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        InitializePlot();

        DataContext = serviceProvider.GetService<MainViewModel>();
        WeakReferenceMessenger.Default.RegisterAll(this);

        // 初始化并启动 30 FPS 渲染定时器
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
        // 设置坐标轴刻度数字(TickLabels)的高清字体大小
        DataPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 14;
        DataPlot.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
        // 设置坐标轴标题(Label)的高清字体大小
        DataPlot.Plot.Axes.Bottom.Label.FontSize = 16;
        DataPlot.Plot.Axes.Left.Label.FontSize = 16;
        // 设置图表顶部主标题的高清字体大小
        DataPlot.Plot.Axes.Title.Label.FontSize = 20;
        // 开启 ScottPlot 的高性能抗锯齿渲染模式
        DataPlot.Plot.Axes.AutoScale();
        DataPlot.Refresh();
    }

    /// <summary>订阅流通信总线分发的强类型遥测</summary>
    public void Receive(TelemetryReceivedMessage message)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_streamers.TryGetValue(message.PortName, out var streamer))
            {
                streamer = DataPlot.Plot.Add.DataStreamer(1000);
                streamer.LineWidth = 2;
                streamer.Color = _streamerColors[_streamers.Count % _streamerColors.Length];

                _streamers[message.PortName] = streamer;
                DataPlot.Plot.Axes.AutoScale();
            }

            streamer.Add(message.Temperature);
        });
    }
}
