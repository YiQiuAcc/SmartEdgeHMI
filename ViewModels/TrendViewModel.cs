using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Services;

namespace SmartEdgeHMI.ViewModels;

public partial class TrendViewModel(ITelemetryRepository telemetryRepo) : ViewModelBase
{
    [ObservableProperty]
    private DateTime _startTime = DateTime.Now.AddHours(-1);

    [ObservableProperty]
    private DateTime _endTime = DateTime.Now;

    [ObservableProperty]
    private int _targetPoints = 1000;

    [ObservableProperty]
    private bool _isHistoryMode;

    public static int[] TargetPointOptions { get; } = [500, 1000, 2000, 5000];

    [RelayCommand]
    private async Task LoadTrendAsync()
    {
        try
        {
            Log.Information("加载历史趋势: {From} ~ {To}, 目标点数 {Points}",
                StartTime, EndTime, TargetPoints);

            var data = await telemetryRepo.GetTelemetryHistoryAsync(StartTime, EndTime, TargetPoints);

            WeakReferenceMessenger.Default.Send(new TrendDataLoadedMessage(Constants.AppConstants.DefaultDeviceName, data));

            Log.Information("历史趋势加载完成, 返回 {Count} 个数据点", data.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载历史趋势失败");
        }
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsHistoryMode = !IsHistoryMode;
    }

    partial void OnIsHistoryModeChanged(bool value)
    {
        if (!value)
        {
            // 切回实时模式时发送空数据通知清理图表
            WeakReferenceMessenger.Default.Send(new TrendDataLoadedMessage(string.Empty, []));
        }
    }
}
