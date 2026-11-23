using System.ComponentModel;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.ViewModels;

/// <summary>图表视图模型: 转发实时点与历史数据, 不直接操作 ScottPlot 控件</summary>
/// <remarks>
/// 事件在调用方线程上同步触发。实时点的来源是串口读线程(经 <see cref="IDeviceStateContainer.PropertyChanged"/>),
/// 因此订阅方必须自行切回 UI 线程后再绘图, 本类不做调度。
/// </remarks>
public class ChartViewModel : ViewModelBase, IDisposable
{
    private readonly IDeviceStateContainer _deviceState;
    private bool _disposed;

    /// <summary>新增一个实时采样点(时间戳为本地时间, 取值为 °C)</summary>
    public event Action<DateTime, double>? LiveDataPointAdded;

    /// <summary>历史数据到达, 订阅方应先清空再批量绘制</summary>
    public event Action<IReadOnlyList<SensorReadingRecord>>? HistoryDataLoaded;

    /// <summary>图表被清空(切换历史/实时, 或重新加载数据前)</summary>
    public event Action? ChartCleared;

    public ChartViewModel(IDeviceStateContainer deviceState)
    {
        _deviceState = deviceState;
        _deviceState.PropertyChanged += OnDeviceStateChanged;
    }

    private void OnDeviceStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.PropertyName == nameof(IDeviceStateContainer.LatestTemperature))
        {
            LiveDataPointAdded?.Invoke(DateTime.Now, _deviceState.LatestTemperature.Celsius);
        }
    }

    /// <summary>载入历史数据时先清空再推送</summary>
    public void LoadHistory(IReadOnlyList<SensorReadingRecord> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ChartCleared?.Invoke();
        if (data.Count > 0)
            HistoryDataLoaded?.Invoke(data);
    }

    /// <summary>退出历史回放, 恢复接收实时点</summary>
    public void ClearToLiveMode()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ChartCleared?.Invoke();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing && _deviceState != null)
        {
            _deviceState.PropertyChanged -= OnDeviceStateChanged;
        }
        _disposed = true;
    }
}
