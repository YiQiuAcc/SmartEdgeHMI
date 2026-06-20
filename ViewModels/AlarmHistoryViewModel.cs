using System.ComponentModel;
using System.Windows.Data;
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

    public BulkObservableCollection<AlarmRecordEntity> AlarmRecords { get; } = [];

    public AlarmHistoryViewModel(ISqliteRepository sqliteRepo)
    {
        _sqliteRepo = sqliteRepo;
        EnableCollectionSynchronization(AlarmRecords);

        var view = CollectionViewSource.GetDefaultView(AlarmRecords);
        view.SortDescriptions.Add(new SortDescription(nameof(AlarmRecordEntity.Timestamp), ListSortDirection.Descending));

        WeakReferenceMessenger.Default.RegisterAll(this);
        _ = LoadAlarmHistorySafeAsync();
    }

    public void Receive(AlarmRecordedMessage message)
    {
        DispatchToUI(() =>
        {
            AlarmRecords.Add(message.Record);
            if (AlarmRecords.Count > AppConstants.MaxLogEntries)
                AlarmRecords.RemoveAt(0);
        });

        // 异步落盘与异常捕获
        _ = SaveAlarmToDbAsync(message.Record);
    }

    private async Task SaveAlarmToDbAsync(AlarmRecordEntity record)
    {
        try
        {
            await _sqliteRepo.SaveAlarmRecordAsync(record);
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
        var data = await _sqliteRepo.GetAlarmHistoryAsync();
        DispatchToUI(() =>
        {
            AlarmRecords.Clear();
            AlarmRecords.AddRange(data);
        });
    }
}
