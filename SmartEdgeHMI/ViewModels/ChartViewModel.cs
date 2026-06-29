using System.ComponentModel;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Database.Entities;
using SmartEdgeHMI.MachineState;

namespace SmartEdgeHMI.ViewModels;

/// <summary>图表 ViewModel: 管理实时/历史数据流, 不直接操作 ScottPlot 控件</summary>
public class ChartViewModel : ViewModelBase, IDisposable
{
    private readonly IDeviceStateContainer _deviceState;

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
        if (e.PropertyName == nameof(IDeviceStateContainer.LatestTemperature))
        {
            LiveDataPointAdded?.Invoke(DateTime.Now, _deviceState.LatestTemperature.Celsius);
        }
    }

    public void LoadHistory(IReadOnlyList<SensorReadingRecord> data)
    {
        ChartCleared?.Invoke();
        if (data.Count > 0)
            HistoryDataLoaded?.Invoke(data);
    }

    public void ClearToLiveMode()
    {
        ChartCleared?.Invoke();
    }

    public void Dispose()
    {
        _deviceState.PropertyChanged -= OnDeviceStateChanged;
    }
}
