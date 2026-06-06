using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.Services;

namespace SmartEdgeHMI.ViewModels;

public partial class MainViewModel : ObservableObject, IRecipient<TelemetryDataMessage>, IRecipient<LogUpdateMessage>
{
    private readonly ISerialPortService _serialPortService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private string? _selectedBaudRate;
    public string StatusColor => IsConnected ? "Green" : "Red";

    public ObservableCollection<string> AvailablePorts { get; set; } = [];
    public ObservableCollection<string> AvailableBaudRate { get; set; } = new(AppConstants.StandardBaudRates);

    [ObservableProperty]
    private double _currentTemperature = 25.0;

    [ObservableProperty]
    private double _alarmThreshold = 50.0;

    private readonly ISqliteRepository _sqliteRepo;

    // 阈值持久化防抖
    private CancellationTokenSource? _saveThresholdCts;
    private static readonly string _appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    [ObservableProperty]
    private ObservableCollection<AlarmHistory> _alarmRecords = new();

    public ObservableCollection<SystemLogModel> SystemLogs { get; } = new();

    public MainViewModel(IConfiguration config, ISqliteRepository sqliteRepo, ISerialPortService serialPortService)
    {
        _serialPortService = serialPortService;
        _sqliteRepo = sqliteRepo;

        // 从配置读取默认值
        var defaultPort = config["HardwareSettings:DefaultComPort"] ?? "COM1";
        var defaultBaud = config["HardwareSettings:DefaultBaudRate"] ?? "9600";
        SelectedPort = defaultPort;
        SelectedBaudRate = defaultBaud;

        if (double.TryParse(config["HardwareSettings:DefaultThreshold"], out var threshold))
            AlarmThreshold = threshold;
        else
            AlarmThreshold = 50.0;

        // 填充可用端口列表
        var systemPorts = _serialPortService.GetAvailablePortNames();
        foreach (var p in systemPorts)
            AvailablePorts.Add(p);
        if (!systemPorts.Contains("COM1")) AvailablePorts.Add("COM1");
        if (!systemPorts.Contains("COM2")) AvailablePorts.Add("COM2");
        if (!systemPorts.Contains("COM3")) AvailablePorts.Add("COM3");

        WeakReferenceMessenger.Default.RegisterAll(this);
        LoadAlarmHistory();
    }

    [RelayCommand]
    private void ToggleConnect()
    {
        if (IsConnected)
            DisconnectPort(SelectedPort, () => IsConnected = false, s => StatusText = s);
        else
            ConnectPort(SelectedPort, SelectedBaudRate, () => IsConnected = true, s => StatusText = s);
    }

    private void ConnectPort(string? portName, string? baudRate,
        Action setConnected, Action<string> updateStatus)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            MessageBox.Show("请先选择串口！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(baudRate, out var baud))
        {
            MessageBox.Show("波特率格式无效！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _serialPortService.OpenPort(portName, baud);
            setConnected();
            updateStatus($"已连接 {portName} @ {baudRate}");
            Log.Information("串口已连接: {Port} @ {BaudRate}", portName, baudRate);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法打开串口 {Port}", portName);
            updateStatus($"连接失败: {portName}");
        }
    }

    private void DisconnectPort(string? portName,
        Action setDisconnected, Action<string> updateStatus)
    {
        if (!string.IsNullOrWhiteSpace(portName))
        {
            _serialPortService.ClosePort(portName);
            Log.Information("串口已断开: {Port}", portName);
        }

        setDisconnected();
        updateStatus("未连接");
    }

    [RelayCommand]
    private void ResetDevice()
    {
        Log.Warning("用户触发设备紧急复位 (DeviceAction.Reset)");

        var cmd = new DeviceCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "Sensor_01",
            Timestamp = DateTime.Now,
            Action = DeviceAction.Reset
        };

        if (IsConnected && !string.IsNullOrWhiteSpace(SelectedPort))
            _serialPortService.SendCommandAsync(SelectedPort, cmd);

        MessageBox.Show("已成功下发复位指令 (DeviceAction.Reset)！", "设备控制",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    partial void OnAlarmThresholdChanged(double value)
    {
        DebounceSaveThreshold(value);
    }

    private async void DebounceSaveThreshold(double value)
    {
        _saveThresholdCts?.Cancel();
        _saveThresholdCts = new CancellationTokenSource();
        var token = _saveThresholdCts.Token;

        try
        {
            await Task.Delay(5000, token);
            await Task.Run(() => SaveThresholdToFile(value), token);
            Log.Information("报警阈值已持久化: {Threshold}°C", value);
        }
        catch (TaskCanceledException)
        {
            // 防抖取消，等待下次变更
        }
    }

    private void SaveThresholdToFile(double value)
    {
        var json = File.ReadAllText(_appSettingsPath);
        var node = JsonNode.Parse(json)!;
        node["HardwareSettings"]!["DefaultThreshold"] = value;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_appSettingsPath, node.ToJsonString(options));
    }

    public void Receive(TelemetryDataMessage message)
    {
        var rawData = message.Value;
        if (double.TryParse(rawData.Replace("T:", "").Trim(), out double temp))
        {
            CurrentTemperature = temp;
            WeakReferenceMessenger.Default.Send(new PlotUpdateMessage(message.PortName, temp));
        }
    }

    public void Receive(LogUpdateMessage message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            SystemLogs.Insert(0, message.LogData);
            if (SystemLogs.Count > 500)
                SystemLogs.RemoveAt(SystemLogs.Count - 1);
        });
    }

    [RelayCommand]
    private void LoadAlarmHistory()
    {
        var data = _sqliteRepo.GetAlarmHistory();
        AlarmRecords = new ObservableCollection<AlarmHistory>(data);
    }
}
