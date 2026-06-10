using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.Models.Entities;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Services;

namespace SmartEdgeHMI.ViewModels;

public partial class AlarmHistoryViewModel : ViewModelBase,
    IRecipient<AlarmRecordedMessage>
{
    private readonly ISqliteRepository _sqliteRepo;

    public BulkObservableCollection<AlarmRecordEntity> AlarmRecords { get; set; } = [];

    public AlarmHistoryViewModel(ISqliteRepository sqliteRepo)
    {
        _sqliteRepo = sqliteRepo;

        EnableCollectionSynchronization(AlarmRecords);
        WeakReferenceMessenger.Default.RegisterAll(this);
        _ = LoadAlarmHistorySafeAsync();
    }

    public void Receive(AlarmRecordedMessage message)
    {
        DispatchToUI(() =>
        {
            AlarmRecords.Insert(0, message.Record);
            if (AlarmRecords.Count > AppConstants.MaxLogEntries)
                AlarmRecords.RemoveAt(AlarmRecords.Count - 1);
        });

        _sqliteRepo.SaveAlarmRecordAsync(message.Record)
            .ContinueWith(t => Log.Error(t.Exception, "报警数据本地落盘失败"), TaskContinuationOptions.OnlyOnFaulted);
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
        var data = await _sqliteRepo.GetAlarmHistoryAsync();
        DispatchToUI(() =>
        {
            AlarmRecords.Clear();
            AlarmRecords.AddRange(data);
        });
    }
}
