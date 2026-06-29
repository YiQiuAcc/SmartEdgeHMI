using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Messaging;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.MachineState;
using SmartEdgeHMI.Utils.Logging;
using SmartEdgeHMI.Utils.UI;
using SmartEdgeHMI.Models.Messages;

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
            if (SystemLogs.Count > _settingsService.Current.Logging.MaxLogEntries)
                SystemLogs.RemoveAt(0);
        });
    }
}
