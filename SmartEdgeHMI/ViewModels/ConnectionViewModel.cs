using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Protocols;
using SmartEdgeHMI.Protocols.Transports.Serial;

namespace SmartEdgeHMI.ViewModels;

public partial class ConnectionViewModel : ViewModelBase, IProtocolConfig
{
    private readonly ISerialPortService _serialPortService;
    private readonly ISettingsService _settingsService;
    private readonly HashSet<string> _connectedPorts = [];

    public bool IsConnected => _connectedPorts.Count > 0;
    public bool IsSelectedPortConnected => SelectedPort is not null && _connectedPorts.Contains(SelectedPort);
    public string ToggleButtonText => IsSelectedPortConnected ? "断开" : "连接";

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(IsSelectedPortConnected))]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    private string? _selectedPort;

    [ObservableProperty]
    private string? _selectedBaudRate;

    [ObservableProperty]
    private byte _modbusSlaveId = 1;

    public string StatusColor => IsConnected ? "Green" : "Red";

    public ObservableCollection<string> AvailablePorts { get; set; }
    public ObservableCollection<string> AvailableBaudRate { get; } = new(AppConstants.StandardBaudRates);

    [ObservableProperty]
    private CommunicationProtocol _selectedProtocol = CommunicationProtocol.JSON;

    public ObservableCollection<CommunicationProtocol> AvailableProtocols { get; } =
        [CommunicationProtocol.JSON, CommunicationProtocol.Modbus];

    public IEnumerable<string> ConnectedPorts => _connectedPorts;

    byte IProtocolConfig.SlaveAddress => ModbusSlaveId;

    public ConnectionViewModel(ISerialPortService serialPortService, ISettingsService settingsService)
    {
        _serialPortService = serialPortService;
        _settingsService = settingsService;

        string[] ports = _serialPortService.GetAvailablePortNames() ?? [];
        AvailablePorts = new ObservableCollection<string>(ports);

        _serialPortService.StateChanged += OnStateChanged;
        LocalizationService.Instance.LanguageChanged += RefreshStatusText;

        LoadSavedSettings();
        TryAutoConnectOnStartup();
    }

    private void OnStateChanged(string portName, ConnectionState state)
    {
        DispatchToUI(() =>
        {
            switch (state)
            {
                case ConnectionState.Connected:
                    SetPortState(portName, connected: true);
                    break;
                case ConnectionState.Disconnected:
                    SetPortState(portName, connected: false);
                    break;
                case ConnectionState.Error:
                    SetPortState(portName, connected: false);
                    StatusText = $"链路故障 [{portName}]";
                    Log.Error("串口 {Port} 链路故障", portName);
                    break;
            }
        });
    }

    private void SetPortState(string portName, bool connected)
    {
        if (connected)
            _connectedPorts.Add(portName);
        else
            _connectedPorts.Remove(portName);

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(IsSelectedPortConnected));
        OnPropertyChanged(nameof(ToggleButtonText));

        string notConnected = FindStringResource("Str_NotConnected", "未连接");
        string connectedTo = FindStringResource("Str_ConnectedTo", "已连接");
        string connectedMulti = FindStringResource("Str_ConnectedMulti", "已连接 {0} 个端口");

        StatusText = _connectedPorts.Count switch
        {
            0 => notConnected,
            1 => $"{connectedTo} {_connectedPorts.First()}",
            _ => connectedMulti.Replace("{0}", _connectedPorts.Count.ToString())
        };
    }

    private static string FindStringResource(string key, string fallback)
        => Application.Current.TryFindResource(key) as string ?? fallback;

    private void LoadSavedSettings()
    {
        try
        {
            var conn = _settingsService.Current.Connection;
            if (!string.IsNullOrEmpty(conn.ComPort))
                SelectedPort = conn.ComPort;
            if (conn.BaudRate > 0)
                SelectedBaudRate = conn.BaudRate.ToString();
            ModbusSlaveId = _settingsService.Current.Modbus.SlaveAddress;
            SelectedProtocol = ParseProtocol(conn.Protocol);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载保存的通信设置失败");
        }
    }

    /// <summary>把持久化的协议字符串解析回枚举; 空串/缺键/非法值(含数字串)一律回退 JSON(现状默认), 不抛异常</summary>
    internal static CommunicationProtocol ParseProtocol(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return CommunicationProtocol.JSON;
        return stored.Trim() switch
        {
            var s when string.Equals(s, nameof(CommunicationProtocol.JSON), StringComparison.OrdinalIgnoreCase)
                => CommunicationProtocol.JSON,
            var s when string.Equals(s, nameof(CommunicationProtocol.Modbus), StringComparison.OrdinalIgnoreCase)
                => CommunicationProtocol.Modbus,
            _ => CommunicationProtocol.JSON
        };
    }

    partial void OnSelectedPortChanged(string? value)
    {
        if (value is not null)
        {
            _settingsService.Current.Connection.ComPort = value;
            _ = SaveSettingsAsync();
        }
    }

    partial void OnSelectedBaudRateChanged(string? value)
    {
        if (value is not null && int.TryParse(value, out int baud))
        {
            _settingsService.Current.Connection.BaudRate = baud;
            _ = SaveSettingsAsync();
        }
    }

    partial void OnModbusSlaveIdChanged(byte value)
    {
        _settingsService.Current.Modbus.SlaveAddress = value;
        _ = SaveSettingsAsync();
    }

    partial void OnSelectedProtocolChanged(CommunicationProtocol value)
    {
        _settingsService.Current.Connection.Protocol = value.ToString();
        _ = SaveSettingsAsync();
    }

    /// <summary>自动连接判定: 上次使用的端口仍在系统枚举里且尚未连接时才允许, 纯静态便于单测</summary>
    internal static bool ShouldAutoConnect(string? savedPort, IReadOnlyCollection<string> availablePorts, bool alreadyConnected)
        => !string.IsNullOrEmpty(savedPort)
           && availablePorts.Contains(savedPort)
           && !alreadyConnected;

    /// <summary>启动后尝试一次自动连接上次使用的串口; 不满足条件只记 INFO, 保持未连接</summary>
    private async Task TryAutoConnectAsync()
    {
        try
        {
            string? savedPort = _settingsService.Current.Connection.ComPort;
            string[] availablePorts = _serialPortService.GetAvailablePortNames() ?? [];
            bool alreadyConnected = savedPort is not null && _connectedPorts.Contains(savedPort);
            if (!ShouldAutoConnect(savedPort, availablePorts, alreadyConnected))
            {
                if (!string.IsNullOrEmpty(savedPort))
                    Log.Information("[AutoConnect] 上次使用的串口 {Port} 当前不在可用列表, 保持未连接", savedPort);
                return;
            }

            Log.Information("[AutoConnect] 自动连接上次使用的串口 {Port}", savedPort);
            await OpenPortAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AutoConnect] 自动连接失败, 保持未连接");
        }
    }

    // 后台线程触发, 不阻塞 UI 启动; 此时 _connectedPorts 尚无任何写入方(状态事件未发生), 判定读取无竞态
    private void TryAutoConnectOnStartup() => _ = Task.Run(TryAutoConnectAsync);

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存通信设置失败");
        }
    }

    private void RefreshStatusText()
    {
        string notConnected = FindStringResource("Str_NotConnected", "未连接");
        string connectedTo = FindStringResource("Str_ConnectedTo", "已连接");
        string connectedMulti = FindStringResource("Str_ConnectedMulti", "已连接 {0} 个端口");

        StatusText = _connectedPorts.Count switch
        {
            0 => notConnected,
            1 => $"{connectedTo} {_connectedPorts.First()}",
            _ => connectedMulti.Replace("{0}", _connectedPorts.Count.ToString())
        };
    }

    [RelayCommand]
    private async Task OpenPortAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort))
        {
            StatusText = "请先选择串口";
            return;
        }

        if (string.IsNullOrEmpty(SelectedBaudRate)) return;

        if (int.TryParse(SelectedBaudRate, out int baud))
        {
            try
            {
                await Task.Run(() => _serialPortService.OpenPort(SelectedPort, baud));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开串口失败: {Port}", SelectedPort);
                StatusText = "打开串口失败";
            }
        }
    }

    [RelayCommand]
    private void ClosePort()
    {
        if (string.IsNullOrEmpty(SelectedPort)) return;
        Log.Information("正在断开串口 {Port}", SelectedPort);
        _serialPortService.ClosePort(SelectedPort);
        Log.Information("串口 {Port} 已断开", SelectedPort);
    }

    [RelayCommand]
    private async Task ToggleConnect()
    {
        if (string.IsNullOrEmpty(SelectedPort))
        {
            StatusText = "请先选择串口";
            return;
        }
        if (_connectedPorts.Contains(SelectedPort))
            ClosePort();
        else
            await OpenPortAsync();
    }
}
