using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Core.Services;

/// <summary>应用设置服务: 从本地 JSON 文件加载/保存设置, 首次运行时回退到 appsettings.json 默认值</summary>
public class SettingsService : ISettingsService
{
    private AppSettings _currentSettings = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _settingsDirectory;
    private readonly string _filePath;
    private readonly IConfiguration _configuration;
    private static readonly JsonSerializerOptions _saveOptions = new() { WriteIndented = true };

    /// <summary>DI 使用的构造函数: 设置目录固定为 %LOCALAPPDATA%\SmartEdgeHMI</summary>
    public SettingsService(IConfiguration configuration)
        : this(configuration, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartEdgeHMI"))
    {
    }

    /// <summary>测试用构造函数: 允许指定设置目录, 便于在临时目录中验证首启行为</summary>
    internal SettingsService(IConfiguration configuration, string settingsDirectory)
    {
        _configuration = configuration;
        _settingsDirectory = settingsDirectory;
        _filePath = Path.Combine(settingsDirectory, "settings.json");
        LoadSettings();
    }

    public AppSettings Current => _currentSettings;

    /// <summary>从本地 JSON 文件加载设置；文件不存在时从 appsettings.json 回退</summary>
    public void LoadSettings()
    {
        if (!File.Exists(_filePath))
        {
            Log.Information("设置文件不存在, 从 appsettings.json 加载默认值: {Path}", _filePath);
            ApplyDefaultsFromConfiguration();

            // 首启落盘 fail-soft: 目录建不出来或写盘失败时只记 Warning, 继续使用内存默认值, 不中断启动;
            // 本次运行中在界面上修改设置时的保存仍会失败, 由调用方现有 catch 记日志。
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "创建设置目录失败, 本次运行将继续使用内存默认值: {Path}", _settingsDirectory);
                return;
            }

            try
            {
                string defaultJson = JsonSerializer.Serialize(_currentSettings, _saveOptions);
                File.WriteAllText(_filePath, defaultJson);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "首次写入设置文件失败, 本次运行将继续使用内存默认值: {Path}", _filePath);
            }

            return;
        }

        _lock.Wait();
        try
        {
            string json = File.ReadAllText(_filePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded != null)
            {
                _currentSettings = loaded;
                Log.Information("已加载设置文件: {Path}", _filePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载设置文件失败: {Path}", _filePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ApplyDefaultsFromConfiguration()
    {
        var modbus = _configuration.GetSection("Modbus");
        if (byte.TryParse(modbus["SlaveAddress"], out byte addr))
            _currentSettings.Modbus.SlaveAddress = addr;
        if (int.TryParse(modbus["PollingIntervalMs"], out int pollMs))
            _currentSettings.Modbus.PollingIntervalMs = pollMs;

        var watchdog = _configuration.GetSection("Watchdog");
        if (int.TryParse(watchdog["HeartbeatIntervalMs"], out int hbMs))
            _currentSettings.Watchdog.HeartbeatIntervalMs = hbMs;
        if (int.TryParse(watchdog["ReconnectDelayMs"], out int reconMs))
            _currentSettings.Watchdog.ReconnectDelayMs = reconMs;
        if (int.TryParse(watchdog["ConnectTimeoutMs"], out int connMs))
            _currentSettings.Watchdog.ConnectTimeoutMs = connMs;

        var ui = _configuration.GetSection("UI");
        if (int.TryParse(ui["ChartRefreshRateMs"], out int rate))
            _currentSettings.UI.ChartRefreshRateMs = rate;

        var hw = _configuration.GetSection("Hardware");
        if (double.TryParse(hw["DefaultThreshold"], out double th))
            _currentSettings.Hardware.DefaultThreshold = th;

        var alarm = _configuration.GetSection("Alarm");
        if (int.TryParse(alarm["RecoveryDebounceCount"], out int debounce))
            _currentSettings.Alarm.RecoveryDebounceCount = debounce;

        var hmiEngine = _configuration.GetSection("HmiEngine");
        if (int.TryParse(hmiEngine["MaxLogEntries"], out int maxLog))
            _currentSettings.Logging.MaxLogEntries = maxLog;
    }

    /// <summary>异步保存当前设置到本地 JSON 文件(原子写入: 先写 .tmp 再 rename)</summary>
    public async Task SaveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _lock.WaitAsync(token);
        try
        {
            string json = JsonSerializer.Serialize(_currentSettings, _saveOptions);
            string tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, token);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
