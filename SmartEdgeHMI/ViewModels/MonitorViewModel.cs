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
    private readonly IAlarmRepository _alarmRepo;
    private readonly IDeviceStateContainer _deviceState;

    private CancellationTokenSource? _saveThresholdCts;
    private bool _isInitializing;

    public double CurrentTemperature => _deviceState.LatestTemperature.Celsius;

    [ObservableProperty]
    private double _alarmThreshold = 50.0;

    [ObservableProperty]
    private bool _hasPendingAlarms;

    public MonitorViewModel(
        ISettingsService settingsService,
        IDeviceCommunicationCoordinator coordinator,
        IAlarmStateMachine alarmStateMachine,
        ITelemetryRepository telemetryRepo,
        IAlarmRepository alarmRepo,
        IDeviceStateContainer deviceState)
    {
        _settingsService = settingsService;
        _coordinator = coordinator;
        _alarmStateMachine = alarmStateMachine;
        _telemetryRepo = telemetryRepo;
        _alarmRepo = alarmRepo;
        _deviceState = deviceState;

        _deviceState.PropertyChanged += OnDeviceStateChanged;
        _alarmStateMachine.AlarmStatesChanged += OnAlarmStatesChanged;

        WeakReferenceMessenger.Default.RegisterAll(this);
        LoadSavedThreshold();
    }

    private void OnDeviceStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDeviceStateContainer.LatestTemperature))
            OnPropertyChanged(nameof(CurrentTemperature));
    }

    private void OnAlarmStatesChanged()
    {
        HasPendingAlarms = _alarmStateMachine.PendingAlarms.Count > 0;
        _deviceState.UpdateActiveAlarms(_alarmStateMachine.ActiveAlarms);
        _ = SyncAlarmStatesToDbAsync();
    }

    private async Task SyncAlarmStatesToDbAsync()
    {
        try
        {
            await _alarmRepo.UpdateAlarmStatesAsync(_alarmStateMachine.PendingAlarms);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "同步报警状态到数据库失败");
        }
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

    private async Task<long> HandleNewAlarmAsync(AlarmRecord alarmRecord)
    {
        _deviceState.UpdateActiveAlarms(_alarmStateMachine.ActiveAlarms);

        long id = await _alarmRepo.SaveAlarmRecordAsync(alarmRecord);
        alarmRecord.Id = id;

        WeakReferenceMessenger.Default.Send(new AlarmRecorded(alarmRecord));
        HasPendingAlarms = true;

        Log.Warning("报警触发: {DeviceId}, 错误码: {AlarmCode}, Id: {Id}, 触发值: {Value}",
            alarmRecord.DeviceId, alarmRecord.AlarmCode, id, alarmRecord.TriggerValue);

        return id;
    }

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
            _ = HandleNewAlarmAsync(alarmRecord);
    }

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
            _ = HandleNewAlarmAsync(alarmRecord);
    }

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

    private async Task DebounceSaveThreshold(double value)
    {
        if (_saveThresholdCts != null)
        {
            await _saveThresholdCts.CancelAsync();
            _saveThresholdCts.Dispose();
        }
        _saveThresholdCts = new CancellationTokenSource();
        var token = _saveThresholdCts.Token;

        try
        {
            await Task.Delay(AppConstants.SettingsSaveDebounceMs, token);
            await SaveThresholdAsync(value, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // 预期内的取消操作
        }
        catch (Exception ex)
        {
            Log.Error(ex, "阈值防抖保存失败");
        }
    }

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

    private async Task SaveThresholdAsync(double value, CancellationToken token)
    {
        _settingsService.Current.Hardware.DefaultThreshold = value;
        await _settingsService.SaveAsync(token);
        await _coordinator.SendThresholdAsync(value);
    }

    [RelayCommand]
    private void AcknowledgeAllAlarms()
    {
        _alarmStateMachine.AcknowledgeAll();
    }

    [RelayCommand]
    private async Task ResetDeviceAsync()
    {
        await _coordinator.ResetDeviceAsync();
    }
}
