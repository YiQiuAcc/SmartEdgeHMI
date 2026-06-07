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
    private bool _isInitializing;

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

        // 加载历史报警记录
        LoadAlarmHistory();

        // 加载并应用保存的阈值设置
        LoadSavedThreshold();
    }

    private void LoadSavedThreshold()
    {
        try
        {
            string savedThreshold = _settingsService.GetSetting("HardwareSettings:DefaultThreshold");
            if (!string.IsNullOrEmpty(savedThreshold) && double.TryParse(savedThreshold, out double threshold))
            {
                // 设置初始化标志位，防止启动时的属性赋值触发持久化/下发等副作用
                _isInitializing = true;
                AlarmThreshold = threshold;
                _isInitializing = false;
                Log.Information("已从配置文件加载阈值: {Threshold}°C", threshold);
            }
            else
            {
                Log.Information("未找到保存的阈值配置，使用默认值: {Threshold}°C", AlarmThreshold);
            }
        }
        catch (Exception ex)
        {
            _isInitializing = false;
            Log.Error(ex, "加载保存的阈值配置时发生错误");
        }
    }

    /// <summary>消费高性能串口通道分发过来的强类型遥测数据</summary>
    public void Receive(TelemetryReceivedMessage message)
    {
        var payload = message.Value;

        // 安全调度到 UI 线程更新数据绑定绑定属性
        DispatchToUI(() =>
        {
            CurrentTemperature = payload.Temperature;
            // 可以在此处继续绑定 payload.OutputCount 或数据质量状态等
        });

        // 读取下位机已判定的错误码，HMI 仅做"翻译官"角色，不自行判断
        if (payload.ErrorCode != ErrorCode.NoError)
        {
            var alarmRecord = new AlarmRecordEntity
            {
                DeviceId = payload.DeviceId,
                Timestamp = DateTime.Now,
                AlarmCode = payload.ErrorCode.ToString(),
                TriggerValue = payload.Temperature
            };

            DispatchToUI(() =>
            {
                AlarmHistory.Insert(0, alarmRecord);
                if (AlarmHistory.Count > AppConstants.MaxLogEntries)
                    AlarmHistory.RemoveAt(AlarmHistory.Count - 1);
            });

            // 异步存储至边缘 SQLite, 防止阻塞流数据消费
            Task.Run(() => _sqliteRepo.SaveAlarmRecord(alarmRecord))
                .ContinueWith(t => Log.Error(t.Exception, "报警数据本地落盘失败"), TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>接收链路断线感知通知, 平滑切换 UI 状态机</summary>
    public void Receive(DeviceStateChangedMessage message)
    {
        DispatchToUI(() =>
        {
            switch (message.State)
            {
                case ConnectionState.Connected:
                    IsConnected = true;
                    StatusText = $"已连接 {message.PortName}";
                    SelectedPort = message.PortName;
                    // 设备连接成功后，自动下发当前阈值配置
                    _ = Task.Run(async () =>
                    {
                        await SaveThresholdAsync(AlarmThreshold, CancellationToken.None);
                        Log.Information("设备连接后自动下发阈值配置: {Threshold}°C", AlarmThreshold);
                    });
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
        DispatchToUI(() =>
        {
            SystemLogs.Insert(0, message.LogData);
            if (SystemLogs.Count > AppConstants.MaxLogEntries)
                SystemLogs.RemoveAt(SystemLogs.Count - 1);
        });
    }

    /// <summary>安全调度到 UI 线程，应用退出时 Application.Current 为 null 则静默丢弃</summary>
    private static void DispatchToUI(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
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
        // 初始化阶段跳过所有副作用操作
        if (_isInitializing)
            return;

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
            // 使用 CancellationToken.None 确保一旦延迟结束，保存操作不会被后续滑块移动取消
            await SaveThresholdAsync(value, CancellationToken.None);
            if (!string.IsNullOrEmpty(SelectedPort) && IsConnected)
                Log.Information("报警阈值已持久化并下发: {Threshold}°C", value);
            else
                Log.Information("报警阈值已持久化: {Threshold}°C", value);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "配置防抖保存发生未预期故障");
        }
    }

    private async Task SaveThresholdAsync(double value, CancellationToken token)
    {
        await _settingsService.SetSettingsAsync("HardwareSettings:DefaultThreshold", value.ToString(), token);

        // 下发阈值配置到硬件设备
        if (!string.IsNullOrEmpty(SelectedPort) && IsConnected)
        {
            // 修改：直接传递数值而不是对象，以匹配虚拟设备模拟器的期望格式
            var command = new CommandPayload(
                CommandId: Guid.NewGuid(),
                DeviceId: AppConstants.DefaultDeviceName,
                Action: DeviceAction.Configure,
                TimestampUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Parameters: value  // 直接传递阈值数值
            );
            await _serialPortService.SendCommandAsync(SelectedPort, command);
        }
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
