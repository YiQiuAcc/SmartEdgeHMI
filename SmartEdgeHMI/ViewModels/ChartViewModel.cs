using System.ComponentModel;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.ViewModels;

/// <summary>图表 ViewModel: 管理实时/历史数据流, 不直接操作 ScottPlot 控件</summary>
public class ChartViewModel : ViewModelBase, IDisposable
{
    private readonly IDeviceStateContainer _deviceState;
    private bool _disposed;

    public event Action<DateTime, double>? LiveDataPointAdded;

    public event Action<IReadOnlyList<SensorReadingRecord>>? HistoryDataLoaded;

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

    public void LoadHistory(IReadOnlyList<SensorReadingRecord> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ChartCleared?.Invoke();
        if (data.Count > 0)
            HistoryDataLoaded?.Invoke(data);
    }

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
