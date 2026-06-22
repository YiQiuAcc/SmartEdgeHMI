using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.State;

public interface ISettingsService
{
    AppSettings Current { get; }

    void LoadSettings();

    Task SaveAsync(CancellationToken token);
}
