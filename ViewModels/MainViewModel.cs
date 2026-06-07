using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models.DTOs;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Services;

namespace SmartEdgeHMI.ViewModels;

public partial class MainViewModel : ObservableObject,
    IRecipient<TelemetryReceivedMessage>,
    IRecipient<DeviceStateChangedMessage>,
    IRecipient<LogUpdateMessage>
{
    private readonly ISerialPortService _serialPortService;
    private readonly ISqliteRepository _sqliteRepo;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private string? _selectedBaudRate;

    // 根据设备运行状态, 动态映射 UI 颜色 (可根据实际需求扩展)
    public string StatusColor => IsConnected ? "Green" : "Red";

    public ObservableCollection<string> AvailablePorts { get; set; } = [];
    public ObservableCollection<string> AvailableBaudRate { get; set; } = new(AppConstants.StandardBaudRates);

    [ObservableProperty]
    private double _currentTemperature = 25.0;

    [ObservableProperty]
    private double _alarmThreshold = 50.0;

    public ObservableCollection<SystemLogModel> SystemLogs { get; set; } = [];
    public ObservableCollection<AlarmRecordEntity> AlarmHistory { get; set; } = [];

    private CancellationTokenSource? _saveThresholdCts;

    public MainViewModel(
        ISerialPortService serialPortService,
        ISqliteRepository sqliteRepo,
        ISettingsService settingsService)
    {
        _serialPortService = serialPortService;
        _sqliteRepo = sqliteRepo;
        _settingsService = settingsService;

        // 加载可用串口列表
        string[]? ports = _serialPortService.GetAvailablePortNames();
        foreach (string port in ports) AvailablePorts.Add(port);

        // 注册强类型 MVVM 消息接收器
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    /// <summary>消费高性能串口通道分发过来的强类型遥测数据</summary>
    public void Receive(TelemetryReceivedMessage message)
    {
        var payload = message.Value;

        // 安全调度到 UI 线程更新数据绑定绑定属性
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentTemperature = payload.Temperature;
            // 可以在此处继续绑定 payload.OutputCount 或数据质量状态等
        });

        // 异常越限动态捕获（触发落盘实体）
        if (payload.Temperature > AlarmThreshold)
        {
            var alarmRecord = new AlarmRecordEntity
            {
                DeviceId = payload.DeviceId,
                Timestamp = DateTime.Now,
                AlarmCode = ErrorCode.ThresholdExceeded.ToString(),
                TriggerValue = payload.Temperature
            };

            // 异步存储至边缘 SQLite, 防止阻塞流数据消费
            Task.Run(() => _sqliteRepo.SaveAlarmRecord(alarmRecord))
                .ContinueWith(t => Log.Error(t.Exception, "报警数据本地落盘失败"), TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>接收链路断线感知通知, 平滑切换 UI 状态机</summary>
    public void Receive(DeviceStateChangedMessage message)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            switch (message.State)
            {
                case ConnectionState.Connected:
                    IsConnected = true;
                    StatusText = $"已连接 {message.PortName}";
                    break;
                case ConnectionState.Disconnected:
                    IsConnected = false;
                    StatusText = $"断开: {message.PortName}";
                    break;
                case ConnectionState.Error:
                    IsConnected = false;
                    StatusText = $"链路故障: {message.ErrorDetails}";
                    break;
            }
        });
    }

    public void Receive(LogUpdateMessage message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            SystemLogs.Insert(0, message.LogData);
            if (SystemLogs.Count > AppConstants.MaxLogEntries)
                SystemLogs.RemoveAt(SystemLogs.Count - 1);
        });
    }

    [RelayCommand]
    private void OpenPort()
    {
        if (string.IsNullOrEmpty(SelectedPort) || string.IsNullOrEmpty(SelectedBaudRate)) return;

        if (int.TryParse(SelectedBaudRate, out int baud))
        {
            try
            {
                _serialPortService.OpenPort(SelectedPort, baud);
                // 发送状态变更通知, 假定开启即成功, 或等待硬件握手后通过 Service 发出 Connected 消息
                WeakReferenceMessenger.Default.Send(new DeviceStateChangedMessage(SelectedPort, ConnectionState.Connected));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开串口失败: {Port}", SelectedPort);
                StatusText = "打开串口失败";
            }
        }
    }

    [RelayCommand]
    private void ClosePort()
    {
        if (string.IsNullOrEmpty(SelectedPort)) return;
        _serialPortService.ClosePort(SelectedPort);
        WeakReferenceMessenger.Default.Send(new DeviceStateChangedMessage(SelectedPort, ConnectionState.Disconnected));
    }

    [RelayCommand]
    private void ToggleConnect()
    {
        if (IsConnected)
            ClosePort();
        else
            OpenPort();
    }

    [RelayCommand]
    private async Task ResetDeviceAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort)) return;

        // 使用全新的 CommandPayload DTO, 并注入标准原生的 .NET 8 Unix 时间戳
        var command = new CommandPayload(
            CommandId: Guid.NewGuid(),
            DeviceId: AppConstants.DefaultDeviceName,
            Action: DeviceAction.Reset,
            TimestampUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );

        await _serialPortService.SendCommandAsync(SelectedPort, command);
    }

    partial void OnAlarmThresholdChanged(double value)
    {
        DebounceSaveThreshold(value);
    }

    private async void DebounceSaveThreshold(double value)
    {
        _saveThresholdCts?.Cancel();
        _saveThresholdCts?.Dispose();
        _saveThresholdCts = new CancellationTokenSource();
        var token = _saveThresholdCts.Token;

        try
        {
            await Task.Delay(AppConstants.SettingsSaveDebounceMs, token);
            await SaveThresholdAsync(value, token);
            Log.Information("报警阈值已通过服务层持久化: {Threshold}°C", value);
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "配置防抖保存发生未预期故障");
        }
    }

    private async Task SaveThresholdAsync(double value, CancellationToken token)
    {
        await _settingsService.SetSettingsAsync("TempThreshold", value.ToString(), token);
    }

    [RelayCommand]
    private void LoadAlarmHistory()
    {
        var data = _sqliteRepo.GetAlarmHistory();
        AlarmHistory.Clear();
        foreach (var item in data)
        {
            AlarmHistory.Add(item);
        }
    }
}
