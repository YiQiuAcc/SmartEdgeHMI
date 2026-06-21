using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Infrastructure.UI;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.ViewModels;

public partial class AlarmHistoryViewModel : ViewModelBase,
    IRecipient<AlarmRecorded>
{
    private readonly IAlarmRepository _alarmRepo;

    public BulkObservableCollection<AlarmRecord> AlarmRecords { get; } = [];

    public AlarmHistoryViewModel(IAlarmRepository alarmRepo)
    {
        _alarmRepo = alarmRepo;
        EnableCollectionSynchronization(AlarmRecords);

        var view = CollectionViewSource.GetDefaultView(AlarmRecords);
        view.SortDescriptions.Add(new SortDescription(nameof(AlarmRecord.Timestamp), ListSortDirection.Descending));

        WeakReferenceMessenger.Default.RegisterAll(this);
        _ = LoadAlarmHistorySafeAsync();
    }

    public void Receive(AlarmRecorded message)
    {
        DispatchToUI(() =>
        {
            AlarmRecords.Add(message.Record);
            if (AlarmRecords.Count > AppConstants.MaxLogEntries)
                AlarmRecords.RemoveAt(0);
        });

        _ = SaveAlarmToDbAsync(message.Record);
    }

    private async Task SaveAlarmToDbAsync(AlarmRecord record)
    {
        try
        {
            await _alarmRepo.SaveAlarmRecordAsync(record);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "报警数据本地落盘失败");
        }
    }

    private async Task LoadAlarmHistorySafeAsync()
    {
        try
        {
            await LoadAlarmHistoryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动时加载历史报警记录失败");
        }
    }

    [RelayCommand]
    private async Task LoadAlarmHistoryAsync()
    {
        var data = await _alarmRepo.GetAlarmHistoryAsync();
        DispatchToUI(() =>
        {
            AlarmRecords.Clear();
            AlarmRecords.AddRange(data);
        });
    }
}
