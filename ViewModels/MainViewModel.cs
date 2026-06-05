using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Constants;

namespace SmartEdgeHMI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "未连接";
    [ObservableProperty]
    private bool _isConnected;
    public string StatusColor => IsConnected ? "Green" : "Red";
    [ObservableProperty]
    private double _alarmThreshold = 50.0;
    public ObservableCollection<string> AvailablePorts { get; set; } = [];
    public ObservableCollection<string> AvailableBaudRate { get; set; } = new(AppConstants.StandardBaudRates);
    [ObservableProperty]
    private string? _selectedPort;
    [ObservableProperty]
    private string? _selectedBaudRate;

    public MainViewModel(IConfiguration config)
    {
        AvailablePorts.Add("COM1");
        AvailablePorts.Add("COM2");
        AvailablePorts.Add("COM3");
        // 模拟扫描到的串口
        SelectedPort = config["HardwareSettings:DefaultComPort"] ?? "COM1";
        SelectedBaudRate = config["HardwareSettings:DefaultBaudRate"] ?? "9600";
    }
}
