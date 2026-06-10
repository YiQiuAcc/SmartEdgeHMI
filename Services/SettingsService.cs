using System.IO;
using System.Text.Json;
using Serilog;
using SmartEdgeHMI.Models;

namespace SmartEdgeHMI.Services;

public class SettingsService : ISettingsService
{
    private AppSettings _currentSettings = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _saveOptions = new() { WriteIndented = true };

    public SettingsService()
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    }

    public AppSettings Current => _currentSettings;

    public void LoadSettings()
    {
        if (!File.Exists(_filePath))
        {
            Log.Information("设置文件不存在，生成默认配置文件: {Path}", _filePath);
            string defaultJson = JsonSerializer.Serialize(_currentSettings, _saveOptions);
            File.WriteAllText(_filePath, defaultJson);
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

    public async Task SaveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _lock.WaitAsync(token);
        try
        {
            string json = JsonSerializer.Serialize(_currentSettings, _saveOptions);
            // 写到一个临时文件
            string tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, token);
            // 写入成功后覆盖原文件
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
