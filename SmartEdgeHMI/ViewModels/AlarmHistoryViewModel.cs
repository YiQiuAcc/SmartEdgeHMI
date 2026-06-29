using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Database.Entities;
using SmartEdgeHMI.Database.Repositories;
using SmartEdgeHMI.Utils.UI;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.MachineState;

namespace SmartEdgeHMI.ViewModels;

public partial class AlarmHistoryViewModel : ViewModelBase,
    IRecipient<AlarmRecorded>
{
    private readonly IAlarmRepository _alarmRepo;
    private readonly IAlarmStateMachine _alarmStateMachine;
    private readonly ISettingsService _settingsService;

    public BulkObservableCollection<AlarmRecord> AlarmRecords { get; } = [];

    public AlarmHistoryViewModel(IAlarmRepository alarmRepo, IAlarmStateMachine alarmStateMachine,
        ISettingsService settingsService)
    {
        _alarmRepo = alarmRepo;
        _alarmStateMachine = alarmStateMachine;
        _settingsService = settingsService;

        EnableCollectionSynchronization(AlarmRecords);

        var view = CollectionViewSource.GetDefaultView(AlarmRecords);
        view.SortDescriptions.Add(new SortDescription(nameof(AlarmRecord.Timestamp), ListSortDirection.Descending));

        _alarmStateMachine.AlarmStatesChanged += OnAlarmStatesChanged;

        WeakReferenceMessenger.Default.RegisterAll(this);
        _ = LoadAlarmHistorySafeAsync();
    }

    private void OnAlarmStatesChanged()
    {
        DispatchToUI(() => CollectionViewSource.GetDefaultView(AlarmRecords).Refresh());
    }

    public void Receive(AlarmRecorded message)
    {
        DispatchToUI(() =>
        {
            AlarmRecords.Add(message.Record);
            if (AlarmRecords.Count > _settingsService.Current.Logging.MaxLogEntries)
                AlarmRecords.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void AcknowledgeAlarm(string deviceId)
    {
        _alarmStateMachine.Acknowledge(deviceId);
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
}
