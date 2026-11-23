using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Repositories;

namespace SmartEdgeHMI.ViewModels;

/// <summary>趋势页视图模型: 选择时间区间与目标点数, 从仓储取降采样后的历史序列</summary>
/// <remarks>
/// 默认区间在实例构造时按当前时间前 1 小时计算, 视图模型是单例, 长时间运行后该默认值已过期,
/// 实际查询以用户选择的时间为准。数据加载与降采样都在仓储层完成, 本类只负责参数与结果转发。
/// </remarks>
public partial class TrendViewModel(ITelemetryRepository telemetryRepo) : ViewModelBase
{
    [ObservableProperty]
    private DateTime _startTime = DateTime.Now.AddHours(-1);

    [ObservableProperty]
    private DateTime _endTime = DateTime.Now;

    /// <summary>目标点数上限; 超过时仓储按桶取样再降采样</summary>
    [ObservableProperty]
    private int _targetPoints = 1000;

    /// <summary>true 表示图表处于历史回放态, 切换为 false 时发出 <see cref="LiveModeRestored"/></summary>
    [ObservableProperty]
    private bool _isHistoryMode;

    public static int[] TargetPointOptions { get; } = [500, 1000, 2000, 5000];

    public event Action<IReadOnlyList<SensorReadingRecord>>? HistoryDataLoaded;

    public event Action? LiveModeRestored;

    [RelayCommand]
    private async Task LoadTrendAsync()
    {
        try
        {
            Log.Information("加载历史趋势: {From} ~ {To}, 目标点数 {Points}",
                StartTime, EndTime, TargetPoints);

            var data = await telemetryRepo.GetTelemetryHistoryAsync(StartTime, EndTime, TargetPoints);
            HistoryDataLoaded?.Invoke(data);

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
        // 只在退出历史态时通知
        if (!value)
            LiveModeRestored?.Invoke();
    }
}
