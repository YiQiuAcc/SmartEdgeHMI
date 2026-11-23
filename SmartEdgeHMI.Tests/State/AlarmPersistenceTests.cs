using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Tests.State;

/// <summary>「状态机事件, 写入数据库」端到端回归: 真 AlarmStateMachine, 真 SqliteAlarmRepository(临时文件库)</summary>
public class AlarmPersistenceTests : IDisposable
{
    private const string DeviceA = "Sensor_01";

    // 每个测试创建的临时设置目录与临时库文件, Dispose 时统一清理
    private readonly List<string> _tempDirectories = [];
    private readonly List<string> _dbPaths = [];

    // 真状态机, 真临时文件库; 设置目录使用临时目录, 避免测试写真实的 %LOCALAPPDATA% 配置与数据库
    private (AlarmStateMachine Machine, SqliteAlarmRepository Repo, string DbPath) CreateFixture()
    {
        string settingsDirectory = Path.Combine(
            Path.GetTempPath(), "SmartEdgeHMI.Tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(settingsDirectory);
        var settingsService = new SettingsService(new ConfigurationBuilder().Build(), settingsDirectory);

        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        var dbConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DefaultConnection"] = $"Data Source={dbPath}",
                ["DatabaseSettings:QueryLimit"] = "10"
            })
            .Build();

        var factory = new SqliteConnectionFactory(dbConfig);
        factory.InitializeDatabaseAsync().GetAwaiter().GetResult();

        return (new AlarmStateMachine(settingsService), new SqliteAlarmRepository(factory, dbConfig), dbPath);
    }

    /// <summary>故障, 确认, 恢复完成, 从数据库读回 NORMAL 且 ClearedAt / AcknowledgedAt 非空</summary>
    /// <remarks>
    /// 完整链路: 故障触发写入数据库, 订阅 AlarmStatesChanged 在回调里把 PendingAlarms 全量交给 UpdateAlarmStatesAsync
    /// (MonitorViewModel.SyncAlarmStatesToDbAsync 的最小真实等价物), 确认写入数据库, 恢复完成, 重新从数据库查询。
    /// 断言全部落在数据库读回结果上, 不是内存对象
    /// </remarks>
    [Fact]
    public async Task 完整链路_故障确认后恢复走完_数据库读到NORMAL且ClearedAt与AcknowledgedAt非空()
    {
        var (machine, repo, _) = CreateFixture();

        // 与 MonitorViewModel 相同的事件接线: 事件回调里把 PendingAlarms 全量交给仓储;
        // 同步阻塞至写入数据库完成, 让「状态机事件, 写入数据库」严格串行, 最终断言不存在异步时序上的不确定性
        machine.AlarmStatesChanged += () =>
            repo.UpdateAlarmStatesAsync(machine.PendingAlarms).GetAwaiter().GetResult();

        // 1. 制造故障: 产生 UNACK 报警并写入数据库(与 MonitorViewModel.HandleNewAlarmAsync 相同: 保存后把自增 ID 回写进状态机持有的记录)
        var alarm = machine.Evaluate(CreatePayload(DeviceA, ErrorCode.ThresholdExceeded));
        Assert.NotNull(alarm);
        Assert.Equal(AlarmState.UNACK, alarm.State);
        long savedId = await repo.SaveAlarmRecordAsync(alarm);
        Assert.True(savedId > 0);
        alarm.Id = savedId;

        var afterAlarm = Assert.Single(await repo.GetAlarmHistoryAsync());
        Assert.Equal(AlarmState.UNACK, afterAlarm.State);
        Assert.Null(afterAlarm.AcknowledgedAt);
        Assert.Null(afterAlarm.ClearedAt);

        // 2. 操作员确认: 事件回调把 ACK + AcknowledgedAt 同步进库
        machine.Acknowledge(DeviceA);

        var afterAck = Assert.Single(await repo.GetAlarmHistoryAsync());
        Assert.Equal(AlarmState.ACK, afterAck.State);
        Assert.NotNull(afterAck.AcknowledgedAt);
        Assert.Null(afterAck.ClearedAt);

        // 3. 故障恢复完成: 连续 3 条正常遥测达到防抖阈值(RecoveryDebounceCount = 3),
        //    第 3 条触发 ACK→NORMAL 与事件, 回调把 NORMAL + ClearedAt 的最终态同步进库
        for (int i = 0; i < 3; i++)
        {
            machine.Evaluate(CreatePayload(DeviceA, ErrorCode.NoError));
        }

        // 4. 最终态必须能从数据库读回: 3ab7619 的缺陷形态就是这一步读到 ACK / ClearedAt IS NULL
        var final = Assert.Single(await repo.GetAlarmHistoryAsync(), r => r.Id == savedId);
        Assert.Equal(AlarmState.NORMAL, final.State);
        Assert.NotNull(final.ClearedAt);
        Assert.NotNull(final.AcknowledgedAt);
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite 默认连接池会保留已打开的库文件句柄, 须先清池再删除
        SqliteConnection.ClearAllPools();

        foreach (string dbPath in _dbPaths)
        {
            foreach (string file in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                for (int attempt = 0; attempt < 3 && File.Exists(file); attempt++)
                {
                    try { File.Delete(file); }
                    catch (IOException) when (attempt < 2) { Thread.Sleep(100); }
                }
            }
        }

        foreach (string directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // 清理失败不影响测试结果
            }
        }
    }

    private static TelemetryPayload CreatePayload(string deviceId, ErrorCode errorCode) => new()
    {
        DeviceId = deviceId,
        Temperature = Temperature.FromCelsius(25.0),
        Humidity = Humidity.FromPercent(60.0),
        StatusCode = DeviceStatus.Online,
        ErrorCode = errorCode,
        QualityCode = DataQuality.Good
    };
}
