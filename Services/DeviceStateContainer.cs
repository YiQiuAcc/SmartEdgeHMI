using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

public class DeviceStateContainer : IDeviceStateContainer
{
    private readonly ConcurrentDictionary<string, DeviceStateSnapshot> _deviceStates = new();
    private readonly ConcurrentDictionary<string, ErrorCode> _activeAlarms = new();
    private readonly HashSet<string> _connectedPorts = [];

    // ===== INotifyPropertyChanged =====
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ===== 最新遥测摘要（取最后一次更新的端口） =====
    public double LatestTemperature { get; private set; } = 25.0;
    public double LatestHumidity { get; private set; }
    public DeviceStatus LatestDeviceStatus { get; private set; } = DeviceStatus.Online;
    public ErrorCode LatestErrorCode { get; private set; }
    public DataQuality LatestQuality { get; private set; } = DataQuality.Good;
    public DateTime LatestTimestamp { get; private set; } = DateTime.Now;
    public string LatestPortName { get; private set; } = string.Empty;

    // ===== 每端口快照 =====
    public DeviceStateSnapshot? GetDeviceState(string portName) =>
        _deviceStates.TryGetValue(portName, out var snap) ? snap : null;

    // ===== 报警状态 =====
    public IReadOnlyDictionary<string, ErrorCode> ActiveAlarms =>
        new Dictionary<string, ErrorCode>(_activeAlarms);

    public bool HasActiveAlarms => _activeAlarms.Count > 0;

    // ===== 连接状态 =====
    private readonly object _connectionLock = new();

    public bool IsPortConnected(string portName)
    {
        lock (_connectionLock) return _connectedPorts.Contains(portName);
    }

    public IReadOnlySet<string> ConnectedPorts
    {
        get { lock (_connectionLock) return new HashSet<string>(_connectedPorts); }
    }

    // ===== 更新方法（由 MonitorViewModel / 物理层调用） =====

    public void UpdateTelemetry(string portName, double temperature, double humidity,
        DeviceStatus status, ErrorCode error, DataQuality quality)
    {
        var snapshot = new DeviceStateSnapshot(portName, temperature, humidity,
            status, error, quality, DateTime.Now);

        _deviceStates[portName] = snapshot;

        LatestTemperature = temperature;
        LatestHumidity = humidity;
        LatestDeviceStatus = status;
        LatestErrorCode = error;
        LatestQuality = quality;
        LatestTimestamp = snapshot.Timestamp;
        LatestPortName = portName;

        Notify(nameof(LatestTemperature));
        Notify(nameof(LatestHumidity));
        Notify(nameof(LatestDeviceStatus));
        Notify(nameof(LatestErrorCode));
        Notify(nameof(LatestQuality));
        Notify(nameof(LatestTimestamp));
        Notify(nameof(LatestPortName));
    }

    public void UpdateConnectionState(string portName, ConnectionState state)
    {
        lock (_connectionLock)
        {
            if (state == ConnectionState.Connected)
                _connectedPorts.Add(portName);
            else
                _connectedPorts.Remove(portName);
        }
        Notify(nameof(ConnectedPorts));
        Notify(nameof(IsPortConnected));
    }

    public void UpdateActiveAlarms(IReadOnlyDictionary<string, ErrorCode> alarms)
    {
        _activeAlarms.Clear();
        foreach (var kv in alarms)
            _activeAlarms.TryAdd(kv.Key, kv.Value);

        Notify(nameof(ActiveAlarms));
        Notify(nameof(HasActiveAlarms));
    }
}
