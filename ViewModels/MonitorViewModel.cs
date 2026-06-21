using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Communication;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.State;

namespace SmartEdgeHMI.ViewModels;

public partial class MonitorViewModel : ViewModelBase,
    IRecipient<DeviceTelemetry>,
    IRecipient<SensorReading>,
    IRecipient<DeviceStateChanged>
{
    private readonly ISettingsService _settingsService;
    private readonly IDeviceCommunicationCoordinator _coordinator;
    private readonly IAlarmStateMachine _alarmStateMachine;
    private readonly ITelemetryRepository _telemetryRepo;
    private readonly IDeviceStateContainer _deviceState;

    private CancellationTokenSource? _saveThresholdCts;
    private bool _isInitializing;

    public double CurrentTemperature => _deviceState.LatestTemperature.Celsius;

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
        }
        catch (Exception ex)
        {
            _isInitializing = false;
            Log.Error(ex, "加载保存的阈值配置时发生错误");
        }
    }

    /// <summary>
    /// 处理 JSON 协议上报的遥测报文：
    /// 1) 更新设备状态容器 → 2) 持久化到 SQLite → 3) 报警状态机边缘触发判定
    /// </summary>
    public void Receive(DeviceTelemetry message)
    {
        var payload = message.Payload;

        _deviceState.UpdateTelemetry(message.PortName, payload.Temperature,
            payload.Humidity, payload.StatusCode, payload.ErrorCode, payload.QualityCode);

        _ = _telemetryRepo.SaveTelemetryAsync(new SensorReadingRecord
        {
            DeviceId = payload.DeviceId,
            Timestamp = DateTime.Now,
            Temperature = payload.Temperature,
            Humidity = payload.Humidity,
            StatusCode = payload.StatusCode,
            ErrorCode = payload.ErrorCode,
            QualityCode = payload.QualityCode
        });

        var alarmRecord = _alarmStateMachine.Evaluate(payload);
        if (alarmRecord is not null)
        {
            _deviceState.UpdateActiveAlarms(_alarmStateMachine.ActiveAlarms);
            WeakReferenceMessenger.Default.Send(new AlarmRecorded(alarmRecord));
            Log.Warning("报警触发: {DeviceId}, 错误码: {ErrorCode}, 触发值: {Value}",
                alarmRecord.DeviceId, alarmRecord.AlarmCode, alarmRecord.TriggerValue);
        }
    }

    /// <summary>
    /// 处理 Modbus 协议解析后的遥测数据(与 DeviceTelemetry 处理流程一致, 但因协议不同需要额外构造 TelemetryPayload
    /// 以复用报警状态机接口)。
    /// </summary>
    public void Receive(SensorReading message)
    {
        _deviceState.UpdateTelemetry(message.PortName, message.Temperature,
            message.Humidity, message.StatusCode, message.ErrorCode, DataQuality.Good);

        _ = _telemetryRepo.SaveTelemetryAsync(new SensorReadingRecord
        {
            DeviceId = AppConstants.DefaultDeviceName,
            Timestamp = DateTime.Now,
            Temperature = message.Temperature,
            Humidity = message.Humidity,
            StatusCode = message.StatusCode,
            ErrorCode = message.ErrorCode,
            QualityCode = DataQuality.Good
        });

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
            WeakReferenceMessenger.Default.Send(new AlarmRecorded(alarmRecord));
            Log.Warning("报警触发: {DeviceId}, 错误码: {ErrorCode}, 触发值: {Value}",
                alarmRecord.DeviceId, alarmRecord.AlarmCode, alarmRecord.TriggerValue);
        }
    }

    /// <summary>处理设备连接状态变更：由设备状态容器统一管理连接状态, 连接建立时自动下发当前阈值到设备端。</summary>
    public void Receive(DeviceStateChanged message)
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

    /// <summary>阈值变更防抖保存：取消上一次未完成的保存, 延迟指定毫秒后再执行。 防止滑块拖动时频繁触发配置文件 I/O。</summary>
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "阈值防抖保存失败");
        }
    }

    /// <summary>设备连接建立后自动下发当前阈值(不防抖, 立即执行)</summary>
    private async Task SaveThresholdSafeAsync(double value)
    {
        try
        {
            await SaveThresholdAsync(value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备连接后自动下发阈值配置失败");
        }
    }

    /// <summary>持久化阈值到本地文件并下发到所有已连接设备</summary>
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
