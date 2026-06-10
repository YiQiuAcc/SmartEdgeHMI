using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Services;

namespace SmartEdgeHMI.ViewModels;

public partial class MonitorViewModel : ViewModelBase,
    IRecipient<TelemetryReceivedMessage>,
    IRecipient<SensorDataMessage>,
    IRecipient<DeviceStateChangedMessage>
{
    private readonly ISettingsService _settingsService;
    private readonly IDeviceCommunicationCoordinator _coordinator;
    private readonly IAlarmStateMachine _alarmStateMachine;

    private CancellationTokenSource? _saveThresholdCts;
    private bool _isInitializing;

    [ObservableProperty]
    private double _currentTemperature = 25.0;

    [ObservableProperty]
    private double _alarmThreshold = 50.0;

    public MonitorViewModel(
        ISettingsService settingsService,
        IDeviceCommunicationCoordinator coordinator,
        IAlarmStateMachine alarmStateMachine)
    {
        _settingsService = settingsService;
        _coordinator = coordinator;
        _alarmStateMachine = alarmStateMachine;

        WeakReferenceMessenger.Default.RegisterAll(this);
        LoadSavedThreshold();
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

    public void Receive(TelemetryReceivedMessage message)
    {
        var payload = message.Value;
        DispatchToUI(() => CurrentTemperature = payload.Temperature);

        var alarmRecord = _alarmStateMachine.Evaluate(payload);
        if (alarmRecord is not null)
        {
            WeakReferenceMessenger.Default.Send(new AlarmRecordedMessage(alarmRecord));
            Log.Warning("报警触发: {DeviceId}, 错误码: {ErrorCode}, 触发值: {Value}",
                alarmRecord.DeviceId, alarmRecord.AlarmCode, alarmRecord.TriggerValue);
        }
    }

    public void Receive(SensorDataMessage message)
    {
        float temperature = message.Temperature;
        DispatchToUI(() => CurrentTemperature = temperature);
    }

    public void Receive(DeviceStateChangedMessage message)
    {
        if (message.State == ConnectionState.Connected)
            _ = SaveThresholdSafeAsync(AlarmThreshold);
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
