using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Utils.UI;

namespace SmartEdgeHMI.ViewModels;

/// <summary>报警历史页视图模型: 加载历史记录、接收新报警、执行确认</summary>
/// <remarks>
/// 新报警经 <see cref="AlarmRecorded"/> 消息从采集线程送达, 因此 <see cref="Receive"/> 内必须切回 UI 线程再改集合。
/// 列表长度上限复用了日志配置的 <c>Logging:MaxLogEntries</c>, 与其他页共用同一容量阈值。
/// </remarks>
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
        // 状态字段被状态机原地改写, 集合本身没有增删, 因此只能靠刷新视图重读行内容
        DispatchToUI(() => CollectionViewSource.GetDefaultView(AlarmRecords).Refresh());
    }

    public void Receive(AlarmRecorded message)
    {
        DispatchToUI(() =>
        {
            AlarmRecords.Add(message.Record);
            // 单条移除
            if (AlarmRecords.Count > _settingsService.Current.Logging.MaxLogEntries)
                AlarmRecords.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void AcknowledgeAlarm(string deviceId)
    {
        _alarmStateMachine.Acknowledge(deviceId);
    }

    /// <summary>重新加载整页历史(清空后批量填充, 只发一次集合通知)</summary>
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
