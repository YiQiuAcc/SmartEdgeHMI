using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Messaging;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Infrastructure.Logging;
using SmartEdgeHMI.Infrastructure.UI;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.ViewModels;

public class LogConsoleViewModel : ViewModelBase,
    IRecipient<LogUpdate>
{
    public BulkObservableCollection<SystemLogModel> SystemLogs { get; } = [];

    public LogConsoleViewModel()
    {
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
            if (SystemLogs.Count > AppConstants.MaxLogEntries)
                SystemLogs.RemoveAt(0);
        });
    }
}
