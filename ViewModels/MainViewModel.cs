using System.Collections.ObjectModel;
using System.Text.Json;
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
    private readonly ModbusService _modbusService;

    // 多端口连接跟踪
    private readonly HashSet<string> _connectedPorts = [];

    public bool IsConnected => _connectedPorts.Count > 0;
    public bool IsSelectedPortConnected => SelectedPort is not null && _connectedPorts.Contains(SelectedPort);
    public string ToggleButtonText => IsSelectedPortConnected ? "断开" : "连接";

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(IsSelectedPortConnected))]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    private string? _selectedPort;

    [ObservableProperty]
    private string? _selectedBaudRate;

    public string StatusColor => IsConnected ? "Green" : "Red";

    public ObservableCollection<string> AvailablePorts { get; set; }
    public ObservableCollection<string> AvailableBaudRate { get; set; } = new(AppConstants.StandardBaudRates);

    [ObservableProperty]
    private CommunicationProtocol _selectedProtocol = CommunicationProtocol.JSON;

    public ObservableCollection<CommunicationProtocol> AvailableProtocols { get; } =
        [CommunicationProtocol.JSON, CommunicationProtocol.Modbus];

    [ObservableProperty]
    private double _currentTemperature = 25.0;

    [ObservableProperty]
    private double _alarmThreshold = 50.0;

    public ObservableCollection<SystemLogModel> SystemLogs { get; set; } = [];
    public ObservableCollection<AlarmRecordEntity> AlarmHistory { get; set; } = [];

    private CancellationTokenSource? _saveThresholdCts;
    private bool _isInitializing;

    // 报警风暴防护：边缘触发 + 恢复迟滞状态机
    private readonly Dictionary<string, ErrorCode> _activeAlarms = [];
    private readonly Dictionary<string, int> _recoveryCounters = [];

    public MainViewModel(
        ISerialPortService serialPortService,
        ISqliteRepository sqliteRepo,
        ISettingsService settingsService,
        ModbusService modbusService)
    {
        _serialPortService = serialPortService;
        _sqliteRepo = sqliteRepo;
        _settingsService = settingsService;
        _modbusService = modbusService;

        string[] ports = _serialPortService.GetAvailablePortNames() ?? [];
        AvailablePorts = new ObservableCollection<string>(ports);

        WeakReferenceMessenger.Default.RegisterAll(this);

        _ = LoadAlarmHistorySafeAsync();
        LoadSavedThreshold();
    }

    private void LoadSavedThreshold()
    {
        try
        {
            string savedThreshold = _settingsService.GetSetting("HardwareSettings:DefaultThreshold");
            if (!string.IsNullOrEmpty(savedThreshold) && double.TryParse(savedThreshold, out double threshold))
            {
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

    public void Receive(TelemetryReceivedMessage message)
    {
        var payload = message.Value;
        DispatchToUI(() => CurrentTemperature = payload.Temperature);
        EvaluateAlarmState(payload);
    }

    /// <summary>边缘触发报警状态机：仅在状态转换瞬间记录，防止报警风暴</summary>
    private void EvaluateAlarmState(TelemetryPayload payload)
    {
        if (payload.QualityCode == DataQuality.Bad)
            return;

        bool hasError = payload.ErrorCode != ErrorCode.NoError;
        bool isCurrentlyAlarmed = _activeAlarms.ContainsKey(payload.DeviceId);

        if (hasError && !isCurrentlyAlarmed)
        {
            // ↑ 上升沿: Normal → Alarm
            _activeAlarms[payload.DeviceId] = payload.ErrorCode;
            _recoveryCounters.Remove(payload.DeviceId);
            RecordAlarm(payload);
        }
        else if (!hasError && isCurrentlyAlarmed)
        {
            // ↓ 潜在下降沿: 进入恢复迟滞计数
            if (!_recoveryCounters.TryGetValue(payload.DeviceId, out int count))
                count = 0;
            count++;
            _recoveryCounters[payload.DeviceId] = count;

            if (count >= AppConstants.AlarmRecoveryDebounceCount)
            {
                _activeAlarms.Remove(payload.DeviceId);
                _recoveryCounters.Remove(payload.DeviceId);
                Log.Information("报警恢复: {DeviceId}, 连续 {Count} 帧正常", payload.DeviceId, count);
            }
        }
        else if (hasError && isCurrentlyAlarmed)
        {
            // → 报警持续: 重置恢复计数器，防止瞬时正常被误判为恢复
            _recoveryCounters.Remove(payload.DeviceId);
        }
        // else: !hasError && !isCurrentlyAlarmed → 稳态正常，无需处理
    }

    private void RecordAlarm(TelemetryPayload payload)
    {
        var alarmRecord = new AlarmRecordEntity
        {
            DeviceId = payload.DeviceId,
            Timestamp = DateTime.Now,
            AlarmCode = payload.ErrorCode.ToString(),
            TriggerValue = payload.Temperature,
            QualityCode = payload.QualityCode
        };

        DispatchToUI(() =>
        {
            AlarmHistory.Insert(0, alarmRecord);
            if (AlarmHistory.Count > AppConstants.MaxLogEntries)
                AlarmHistory.RemoveAt(AlarmHistory.Count - 1);
        });

        _sqliteRepo.SaveAlarmRecordAsync(alarmRecord)
            .ContinueWith(t => Log.Error(t.Exception, "报警数据本地落盘失败"), TaskContinuationOptions.OnlyOnFaulted);

        Log.Warning("报警触发: {DeviceId}, 错误码: {ErrorCode}, 触发值: {Value}",
            payload.DeviceId, payload.ErrorCode, payload.Temperature);
    }

    public void Receive(DeviceStateChangedMessage message)
    {
        DispatchToUI(() =>
        {
            switch (message.State)
            {
                case ConnectionState.Connected:
                    SetPortState(message.PortName, connected: true);
                    _ = SaveThresholdSafeAsync(AlarmThreshold);
                    break;
                case ConnectionState.Disconnected:
                    SetPortState(message.PortName, connected: false);
                    break;
                case ConnectionState.Error:
                    SetPortState(message.PortName, connected: false);
                    StatusText = $"链路故障 [{message.PortName}]: {message.ErrorDetails}";
                    Log.Error("串口 {Port} 链路故障: {Error}", message.PortName, message.ErrorDetails);
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

    private void SetPortState(string portName, bool connected)
    {
        if (connected)
            _connectedPorts.Add(portName);
        else
            _connectedPorts.Remove(portName);

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(IsSelectedPortConnected));
        OnPropertyChanged(nameof(ToggleButtonText));

        StatusText = _connectedPorts.Count switch
        {
            0 => "未连接",
            1 => $"已连接 {_connectedPorts.First()}",
            _ => $"已连接 {_connectedPorts.Count} 个端口"
        };
    }

    private static void DispatchToUI(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    [RelayCommand]
    private void OpenPort()
    {
        if (string.IsNullOrEmpty(SelectedPort) || string.IsNullOrEmpty(SelectedBaudRate)) return;

        if (int.TryParse(SelectedBaudRate, out int baud))
        {
            try
            {
                Log.Information("正在连接串口 {Port}，波特率 {BaudRate}", SelectedPort, baud);
                _serialPortService.OpenPort(SelectedPort, baud);
                SetPortState(SelectedPort, connected: true);
                WeakReferenceMessenger.Default.Send(new DeviceStateChangedMessage(SelectedPort, ConnectionState.Connected));
                Log.Information("串口 {Port} 连接成功", SelectedPort);
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
        Log.Information("正在断开串口 {Port}", SelectedPort);
        _serialPortService.ClosePort(SelectedPort);
        SetPortState(SelectedPort, connected: false);
        WeakReferenceMessenger.Default.Send(new DeviceStateChangedMessage(SelectedPort, ConnectionState.Disconnected));
        Log.Information("串口 {Port} 已断开", SelectedPort);
    }

    [RelayCommand]
    private void ToggleConnect()
    {
        if (string.IsNullOrEmpty(SelectedPort)) return;
        if (_connectedPorts.Contains(SelectedPort))
            ClosePort();
        else
            OpenPort();
    }

    [RelayCommand]
    private async Task ResetDeviceAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort)) return;

        switch (SelectedProtocol)
        {
            case CommunicationProtocol.JSON:
            {
                var command = new CommandPayload(
                    CommandId: Guid.NewGuid(),
                    DeviceId: AppConstants.DefaultDeviceName,
                    Action: DeviceAction.Reset,
                    TimestampUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                await _serialPortService.WriteStringAsync(SelectedPort, JsonSerializer.Serialize(command));
                Log.Information("[{Protocol}] 设备复位指令已发送至 {Port}", SelectedProtocol, SelectedPort);
                break;
            }
            case CommunicationProtocol.Modbus:
            {
                // 写保持寄存器 0x0001 = 1 触发复位
                await _modbusService.WriteSingleRegisterAsync(SelectedPort,
                    AppConstants.DefaultModbusSlaveAddress, 0x0001, 1);
                Log.Information("[{Protocol}] Modbus 复位指令已发送至 {Port}", SelectedProtocol, SelectedPort);
                break;
            }
        }
    }

    partial void OnAlarmThresholdChanged(double value)
    {
        if (_isInitializing) return;
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

    private async Task SaveThresholdSafeAsync(double value)
    {
        try
        {
            await SaveThresholdAsync(value, CancellationToken.None);
            Log.Information("设备连接后自动下发阈值配置: {Threshold}°C", value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动下发阈值配置失败");
        }
    }

    private async Task SaveThresholdAsync(double value, CancellationToken token)
    {
        await _settingsService.SetSettingsAsync("HardwareSettings:DefaultThreshold", value.ToString(), token);

        // 广播阈值到所有已连接端口
        foreach (string portName in _connectedPorts.ToList())
        {
            switch (SelectedProtocol)
            {
                case CommunicationProtocol.JSON:
                {
                    var command = new CommandPayload(
                        CommandId: Guid.NewGuid(),
                        DeviceId: AppConstants.DefaultDeviceName,
                        Action: DeviceAction.Configure,
                        TimestampUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Parameters: value
                    );
                    await _serialPortService.WriteStringAsync(portName, JsonSerializer.Serialize(command));
                    Log.Information("[JSON] 阈值配置已下发至 {Port}: {Value}°C", portName, value);
                    break;
                }
                case CommunicationProtocol.Modbus:
                {
                    // 写保持寄存器 0x0002 = 阈值 (取整数部分)
                    await _modbusService.WriteSingleRegisterAsync(portName,
                        AppConstants.DefaultModbusSlaveAddress, 0x0002, (ushort)value);
                    Log.Information("[Modbus] 阈值配置已下发至 {Port}: {Value}°C", portName, (ushort)value);
                    break;
                }
            }
        }
    }

    private async Task LoadAlarmHistorySafeAsync()
    {
        try
        {
            await LoadAlarmHistoryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动时加载历史报警记录失败");
        }
    }

    [RelayCommand]
    private async Task LoadAlarmHistoryAsync()
    {
        var data = await _sqliteRepo.GetAlarmHistoryAsync();
        DispatchToUI(() =>
        {
            AlarmHistory.Clear();
            foreach (var item in data)
                AlarmHistory.Add(item);
        });
    }
}
