namespace SmartEdgeHMI.Services;

using SmartEdgeHMI.Models;

public interface ISettingsService
{
    AppSettings Current { get; }

    void LoadSettings();

    Task SaveAsync(CancellationToken token);
}
