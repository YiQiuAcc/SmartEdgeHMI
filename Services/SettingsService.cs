using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace SmartEdgeHMI.Services;

public class SettingsService : ISettingsService
{
    private readonly string _filePath;
    private JsonNode? _settingsCache; // 内存缓存
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SettingsService()
    {
        // 获取当前用户的 LocalAppData 目录
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 为应用创建一个专属文件夹
        string? appFolder = Path.Combine(localAppData, "SmartEdgeHMI");
        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }

        _filePath = Path.Combine(appFolder, "usersettings.json");
    }

    public string GetSetting(string key)
    {
        if (_settingsCache is null) return string.Empty;

        try
        {
            string[] path = key.Split(':');
            JsonNode? node = _settingsCache;
            foreach (string segment in path)
            {
                node = node?[segment];
                if (node is null) return string.Empty;
            }
            return node.GetValue<string>();
        }
        catch
        {
            return string.Empty;
        }
    }

    public void LoadSettings()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                File.WriteAllText(_filePath, "{}");
                _settingsCache = new JsonObject();
            }
            else
            {
                string json = File.ReadAllText(_filePath);
                _settingsCache = JsonNode.Parse(json) ?? new JsonObject();
            }
            Log.Information("Settings loaded into cache from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings, resetting to defaults");
            File.WriteAllText(_filePath, "{}");
        }
    }

    public async Task SetSettingsAsync(string key, string value, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            // 直接操作缓存
            _settingsCache ??= new JsonObject();
            var node = _settingsCache;
            string[]? path = key.Split(':');
            for (int i = 0; i < path.Length - 1; i++)
            {
                string? segment = path[i];
                if (node[segment] is not JsonObject child)
                {
                    child = [];
                    node[segment] = child;
                }
                node = child;
            }

            string? leafKey = path[^1];
            node[leafKey] = JsonValue.Create(value);
            string jsonOut = JsonSerializer.Serialize(_settingsCache, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, jsonOut, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist setting {Key} = {Value}", key, value);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }
}
