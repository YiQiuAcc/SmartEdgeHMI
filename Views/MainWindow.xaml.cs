using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views;

public partial class MainWindow : Window, IRecipient<PlotUpdateMessage>
{
    private readonly Dictionary<string, ScottPlot.Plottables.DataStreamer> _streamers = [];
    private static readonly Color[] _streamerColors = [Colors.Cyan, Colors.Orange];

    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        InitializePlot();
        DataContext = serviceProvider.GetService<MainViewModel>();
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    private void InitializePlot()
    {
        DataPlot.Plot.FigureBackground.Color = Color.FromHex("#2E2E2E");
        DataPlot.Plot.DataBackground.Color = Color.FromHex("#2E2E2E");
        DataPlot.Plot.Axes.Color(Colors.White);
        DataPlot.Refresh();
    }

    public void Receive(PlotUpdateMessage message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_streamers.TryGetValue(message.PortName, out var streamer))
            {
                streamer = DataPlot.Plot.Add.DataStreamer(100);
                streamer.LineWidth = 2;
                streamer.Color = _streamerColors[_streamers.Count % _streamerColors.Length];
                _streamers[message.PortName] = streamer;
                DataPlot.Plot.Axes.AutoScale();
            }

            streamer.Add(message.Temperature);
            DataPlot.Refresh();
        });
    }
}
