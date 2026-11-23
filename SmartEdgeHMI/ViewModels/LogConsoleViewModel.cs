using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Messaging;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Models.Logging;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Utils.UI;

namespace SmartEdgeHMI.ViewModels;

/// <summary>日志控制台视图模型: 接收 Serilog Sink 转发的日志并保持固定条数上限</summary>
/// <remarks>
/// 消息来自任意写日志的线程, 因此入集前统一经 <see cref="ViewModelBase.DispatchToUI"/> 回到 UI 线程,
/// 绑定视图按时间倒序, 新日志插入即出现在顶部。
/// </remarks>
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
                // 批量裁剪: 一次裁掉上限的 1/10, 避免每追加一条日志就触发一次集合整体前移
                for (int i = 0; i < TrimCount(maxEntries); i++)
                    SystemLogs.RemoveAt(0);
            }
        });
    }

    /// <summary>批量裁剪条数: 一次裁掉上限的 1/10, 至少 1 条</summary>
    public static int TrimCount(int max) => Math.Max(1, max / 10);
}
