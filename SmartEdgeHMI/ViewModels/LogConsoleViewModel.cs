using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Messaging;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Models.Logging;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Utils.UI;

namespace SmartEdgeHMI.ViewModels;

public class LogConsoleViewModel : ViewModelBase,
    IRecipient<LogUpdate>
{
    private readonly ISettingsService _settingsService;

    public BulkObservableCollection<SystemLogModel> SystemLogs { get; } = [];

    public LogConsoleViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        EnableCollectionSynchronization(SystemLogs);

        var view = CollectionViewSource.GetDefaultView(SystemLogs);
        view.SortDescriptions.Add(new SortDescription(nameof(SystemLogModel.Timestamp), ListSortDirection.Descending));

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public void Receive(LogUpdate message)
    {
        DispatchToUI(() =>
        {
            SystemLogs.Add(message.LogData);

            int maxEntries = _settingsService.Current.Logging.MaxLogEntries;
            if (SystemLogs.Count > maxEntries)
            {
                // 批量裁剪: 一次裁掉上限的 1/10(至少 1 条), 避免每来一条日志就令集合整体前移一次
                for (int i = 0; i < TrimCount(maxEntries); i++)
                    SystemLogs.RemoveAt(0);
            }
        });
    }

    /// <summary>批量裁剪条数: 超出上限时一次裁掉上限的 1/10, 至少 1 条 (例如上限 500 → 一次裁 50 条)</summary>
    public static int TrimCount(int max) => Math.Max(1, max / 10);
}
