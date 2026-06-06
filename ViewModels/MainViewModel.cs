using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models;

namespace SmartEdgeHMI.ViewModels;

public partial class MainViewModel : ObservableObject,IRecipient<TelemetryDataMessage>
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusColor))] // 当连接状态改变时，通知通知颜色属性也刷新
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "未连接";
    // 状态灯颜色（只读计算属性）
    public string StatusColor => IsConnected ? "Green" : "Red";

    // COM口与波特率
    public ObservableCollection<string> AvailablePorts { get; set; } = [];
    [ObservableProperty]
    private string? _selectedPort;
    public ObservableCollection<string> AvailableBaudRate { get; set; } = new(AppConstants.StandardBaudRates);
    [ObservableProperty]
    private string? _selectedBaudRate;
    [ObservableProperty]
    private double _currentTemperature = 25.0;
    [ObservableProperty]
    private double _alarmThreshold = 50.0;

    public MainViewModel(IConfiguration config)
    {
        SelectedPort = config["HardwareSettings:DefaultComPort"] ?? "COM1";
        SelectedBaudRate = config["HardwareSettings:DefaultBaudRate"] ?? "9600";
        if (double.TryParse(config["HardwareSettings:DefaultThreshold"], out var threshold))
        {
            AlarmThreshold = threshold;
        }
        else
        {
            AlarmThreshold = 50.0;
        }
        AvailablePorts.Add("COM1");
        AvailablePorts.Add("COM2");
        AvailablePorts.Add("COM3");
        WeakReferenceMessenger.Default.Register(this);
    }

    [RelayCommand]
    private void ToggleConnect()
    {
        IsConnected = !IsConnected;
        StatusText = IsConnected ? $"已连接到 {SelectedPort} ({SelectedBaudRate})" : "未连接";
    }

    [RelayCommand]
    private void ResetDevice()
    {
        // 触发紧急复位的动作 (下发 DeviceAction.Reset)
        System.Windows.MessageBox.Show($"已成功下发复位指令 (DeviceAction.Reset)！", "设备控制", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    public void Receive(TelemetryDataMessage message)
    {
        var rawData = message.Value;
        if(double.TryParse(rawData.Replace("T:","").Trim(),out double temp))
        {
            CurrentTemperature = temp;
            WeakReferenceMessenger.Default.Send(new PlotUpdateMessage(temp));
        }
    }
}
