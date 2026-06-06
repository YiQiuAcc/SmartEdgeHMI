using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views;

public partial class MainWindow : Window, IRecipient<PlotUpdateMessage>
{
    private ScottPlot.Plottables.DataStreamer? _streamer;

    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        InitializePlot();
        DataContext = serviceProvider.GetService<MainViewModel>();
        WeakReferenceMessenger.Default.Register(this);
    }

    private void InitializePlot()
    {
        _streamer = DataPlot.Plot.Add.DataStreamer(100);
        _streamer.Color = Colors.Blue;
        _streamer.LineWidth = 2;
        DataPlot.Plot.FigureBackground.Color = Color.FromHex("#2E2E2E");
        DataPlot.Plot.DataBackground.Color = Color.FromHex("#2E2E2E");
        DataPlot.Plot.Axes.Color(Colors.White); // 坐标轴变成白色
        DataPlot.Refresh();
    }

    public void Receive(PlotUpdateMessage message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 往流里追加新温度
            _streamer?.Add(message.Temperature);
            // 刷新图表
            DataPlot.Refresh();
        });
    }
}
