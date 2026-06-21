using System.ComponentModel;
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

public partial class MonitorViewModel : ViewModelBase,
    IRecipient<DeviceTelemetryMessage>,
    IRecipient<SensorReadingMessage>,
    IRecipient<DeviceStateChangedMessage>
{
    private readonly ISettingsService _settingsService;
    private readonly IDeviceCommunicationCoordinator _coordinator;
    private readonly IAlarmStateMachine _alarmStateMachine;
    private readonly ITelemetryRepository _telemetryRepo;
    private readonly IDeviceStateContainer _deviceState;

    private CancellationTokenSource? _saveThresholdCts;
    private bool _isInitializing;

    /// <summary>代理到 StateContainer — 保持 XAML 绑定兼容</summary>
    public double CurrentTemperature => _deviceState.LatestTemperature;

    [ObservableProperty]
    private double _alarmThreshold = 50.0;

    public MonitorViewModel(
        ISettingsService settingsService,
        IDeviceCommunicationCoordinator coordinator,
        IAlarmStateMachine alarmStateMachine,
        ITelemetryRepository telemetryRepo,
        IDeviceStateContainer deviceState)
    {
        _settingsService = settingsService;
        _coordinator = coordinator;
        _alarmStateMachine = alarmStateMachine;
        _telemetryRepo = telemetryRepo;
        _deviceState = deviceState;

        _deviceState.PropertyChanged += OnDeviceStateChanged;

        WeakReferenceMessenger.Default.RegisterAll(this);
        LoadSavedThreshold();
    }

    private void OnDeviceStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDeviceStateContainer.LatestTemperature))
            OnPropertyChanged(nameof(CurrentTemperature));
    }

    private void LoadSavedThreshold()
    {
        try
        {
            double threshold = _settingsService.Current.Hardware.DefaultThreshold;
            _isInitializing = true;
            AlarmThreshold = threshold;
            _isInitializing = false;
            Log.Information("已从配置文件加载阈值: {Threshold}°C", threshold);
        }
        catch (Exception ex)
        {
            _isInitializing = false;
            Log.Error(ex, "加载保存的阈值配置时发生错误");
        }
    }

    public void Receive(DeviceTelemetryMessage message)
    {
        var payload = message.Payload;

        // 更新全局状态容器
        _deviceState.UpdateTelemetry(message.PortName, payload.Temperature,
            payload.Humidity, payload.StatusCode, payload.ErrorCode, payload.QualityCode);

        // 持久化到 SQLite
        _ = _telemetryRepo.SaveTelemetryAsync(new SensorReadingEntity
        {
            DeviceId = payload.DeviceId,
            Timestamp = DateTime.Now,
            Temperature = payload.Temperature,
            Humidity = payload.Humidity,
            StatusCode = payload.StatusCode,
            ErrorCode = payload.ErrorCode,
            QualityCode = payload.QualityCode
        });

        // 报警评估（业务逻辑不变）
        var alarmRecord = _alarmStateMachine.Evaluate(payload);
        if (alarmRecord is not null)
        {
            _deviceState.UpdateActiveAlarms(_alarmStateMachine.ActiveAlarms);
            WeakReferenceMessenger.Default.Send(new AlarmRecordedMessage(alarmRecord));
            Log.Warning("报警触发: {DeviceId}, 错误码: {ErrorCode}, 触发值: {Value}",
                alarmRecord.DeviceId, alarmRecord.AlarmCode, alarmRecord.TriggerValue);
        }
    }

    public void Receive(SensorReadingMessage message)
    {
        // 更新全局状态容器
        _deviceState.UpdateTelemetry(message.PortName, message.Temperature,
            message.Humidity, message.StatusCode, message.ErrorCode, DataQuality.Good);

        // 持久化到 SQLite
        _ = _telemetryRepo.SaveTelemetryAsync(new SensorReadingEntity
        {
            DeviceId = AppConstants.DefaultDeviceName,
            Timestamp = DateTime.Now,
            Temperature = message.Temperature,
            Humidity = message.Humidity,
            StatusCode = message.StatusCode,
            ErrorCode = message.ErrorCode,
            QualityCode = DataQuality.Good
        });

        // 构造 TelemetryPayload 供报警状态机评估
        var payload = new TelemetryPayload
        {
            DeviceId = AppConstants.DefaultDeviceName,
            Temperature = message.Temperature,
            Humidity = message.Humidity,
            StatusCode = message.StatusCode,
            ErrorCode = message.ErrorCode,
            QualityCode = DataQuality.Good
        };

        var alarmRecord = _alarmStateMachine.Evaluate(payload);
        if (alarmRecord is not null)
        {
            _deviceState.UpdateActiveAlarms(_alarmStateMachine.ActiveAlarms);
            WeakReferenceMessenger.Default.Send(new AlarmRecordedMessage(alarmRecord));
            Log.Warning("报警触发: {DeviceId}, 错误码: {ErrorCode}, 触发值: {Value}",
                alarmRecord.DeviceId, alarmRecord.AlarmCode, alarmRecord.TriggerValue);
        }
    }

    public void Receive(DeviceStateChangedMessage message)
    {
        _deviceState.UpdateConnectionState(message.PortName, message.State);

        if (message.State == ConnectionState.Connected)
            _ = SaveThresholdSafeAsync(AlarmThreshold);
    }

    partial void OnAlarmThresholdChanged(double value)
    {
        if (_isInitializing) return;
        _ = DebounceSaveThreshold(value);
    }

    private async Task DebounceSaveThreshold(double value)
    {
        _saveThresholdCts?.Cancel();
        _saveThresholdCts?.Dispose();
        _saveThresholdCts = new CancellationTokenSource();
        var token = _saveThresholdCts.Token;

        try
        {
            await Task.Delay(AppConstants.SettingsSaveDebounceMs, token);
            await SaveThresholdAsync(value, CancellationToken.None);
            Log.Information("报警阈值已持久化并下发: {Threshold}°C", value);
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
        _settingsService.Current.Hardware.DefaultThreshold = value;
        await _settingsService.SaveAsync(token);
        await _coordinator.SendThresholdAsync(value);
    }

    [RelayCommand]
    private async Task ResetDeviceAsync()
    {
        await _coordinator.ResetDeviceAsync();
    }
}
