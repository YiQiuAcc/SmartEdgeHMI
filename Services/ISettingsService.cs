namespace SmartEdgeHMI.Services;

public interface ISettingsService
{
    public string GetSetting(string key);

    public void LoadSettings();

    public Task SetSettingsAsync(string key, string value, CancellationToken token);
}
