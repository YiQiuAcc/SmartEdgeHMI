using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views;

public partial class MainWindow : Window, IRecipient<TelemetryReceivedMessage>
{
    private readonly Dictionary<string, ScottPlot.Plottables.DataStreamer> _streamers = [];

    // 工业级看版的暗色调高端调色盘
    private static readonly Color[] _streamerColors = [Colors.Cyan, Colors.Gold];

    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        InitializePlot();

        DataContext = serviceProvider.GetService<MainViewModel>();
        // 自动注册当前 View 实例实现的所有 IRecipient 接口
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    private void InitializePlot()
    {
        DataPlot.Plot.FigureBackground.Color = Color.FromHex("#1E1E1E");
        DataPlot.Plot.DataBackground.Color = Color.FromHex("#252526");
        DataPlot.Plot.Axes.Color(Colors.LightGray);
        // 设置坐标轴刻度数字（TickLabels）的高清字体大小
        DataPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 14;
        DataPlot.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
        // 设置坐标轴标题（Label）的高清字体大小（如果你有设置 X/Y 轴名称的话）
        DataPlot.Plot.Axes.Bottom.Label.FontSize = 16;
        DataPlot.Plot.Axes.Left.Label.FontSize = 16;
        // 设置图表顶部主标题的高清字体大小
        DataPlot.Plot.Axes.Title.Label.FontSize = 20;
        // 开启 ScottPlot 的高性能抗锯齿渲染模式
        DataPlot.Plot.Axes.AutoScale();
        DataPlot.Refresh();
    }

    /// <summary>直接订阅流通信总线分发的强类型遥测，用于百万级点数实时推流渲染</summary>
    public void Receive(TelemetryReceivedMessage message)
    {
        // ScottPlot 必须在 UI 线程执行更新操作
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_streamers.TryGetValue(message.PortName, out var streamer))
            {
                // 初始化定长 1000 个采样点的环形流渲染器 (DataStreamer)
                streamer = DataPlot.Plot.Add.DataStreamer(1000);
                streamer.LineWidth = 2;
                streamer.Color = _streamerColors[_streamers.Count % _streamerColors.Length];

                _streamers[message.PortName] = streamer;
                DataPlot.Plot.Axes.AutoScale();
            }

            // 极为干净地推入强类型数据点
            streamer.Add(message.Temperature);

            // 高频流下建议减少全量 Refresh 频率（例如通过计数器控制渲染帧率），或保持默认以获得极致实时感
            DataPlot.Refresh();
        });
    }
}
