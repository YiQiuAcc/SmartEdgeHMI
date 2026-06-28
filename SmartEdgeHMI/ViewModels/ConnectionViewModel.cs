using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Communication;
using SmartEdgeHMI.Communication.Ports;
using SmartEdgeHMI.Infrastructure;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.State;

namespace SmartEdgeHMI.ViewModels;

public partial class ConnectionViewModel : ViewModelBase,
    IRecipient<DeviceStateChanged>,
    IProtocolConfig
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

        LocalizationService.Instance.LanguageChanged += RefreshStatusText;
        WeakReferenceMessenger.Default.RegisterAll(this);

        LoadSavedSettings();
    }

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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载保存的通信设置失败");
        }
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

    public void Receive(DeviceStateChanged message)
    {
        DispatchToUI(() =>
        {
            switch (message.State)
            {
                case ConnectionState.Connected:
                    SetPortState(message.PortName, connected: true);
                    break;
                case ConnectionState.Disconnected:
                    SetPortState(message.PortName, connected: false);
                    break;
                case ConnectionState.Error:
                    SetPortState(message.PortName, connected: false);
                    StatusText = $"链路故障 [{message.PortName}]: {message.ErrorDetails}";
                    Log.Error("串口 {Port} 链路故障: {Error}", message.PortName, message.ErrorDetails);
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

    [RelayCommand]
    private async Task OpenPortAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort) || string.IsNullOrEmpty(SelectedBaudRate)) return;

        if (int.TryParse(SelectedBaudRate, out int baud))
        {
            try
            {
                Log.Information("正在连接串口 {Port}, 波特率 {BaudRate}", SelectedPort, baud);
                await Task.Run(() => _serialPortService.OpenPort(SelectedPort, baud));
                Log.Information("串口 {Port} 连接成功", SelectedPort);
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
        if (string.IsNullOrEmpty(SelectedPort)) return;
        if (_connectedPorts.Contains(SelectedPort))
            ClosePort();
        else
            await OpenPortAsync();
    }
}
