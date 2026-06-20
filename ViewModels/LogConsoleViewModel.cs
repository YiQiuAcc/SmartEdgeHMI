using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Messaging;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.ViewModels;

public partial class LogConsoleViewModel : ViewModelBase,
    IRecipient<LogUpdateMessage>
{
    public BulkObservableCollection<SystemLogModel> SystemLogs { get; } = [];

    public LogConsoleViewModel()
    {
        EnableCollectionSynchronization(SystemLogs);

        var view = CollectionViewSource.GetDefaultView(SystemLogs);
        view.SortDescriptions.Add(new SortDescription(nameof(SystemLogModel.Timestamp), ListSortDirection.Descending));

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public void Receive(LogUpdateMessage message)
    {
        DispatchToUI(() =>
        {
            SystemLogs.Add(message.LogData);
            if (SystemLogs.Count > AppConstants.MaxLogEntries)
                SystemLogs.RemoveAt(0);
        });
    }
}
