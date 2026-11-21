using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Filters;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Tests.Data;

public class SqliteAlarmRepositoryTests
{
    private const string DeviceA = "Dev-A";
    private const string DeviceB = "Dev-B";
    private const string AlarmHigh = "ALM_HIGH";
    private const string AlarmLow = "ALM_LOW";

    // 固定基准时间, 不使用 DateTime.Now: 避免测试撞上真实时钟的日期/小时边界
    private static readonly DateTime _baseTime = new(2026, 1, 10, 12, 0, 0);

    [Fact]
    public async Task GetAlarmHistoryAsync_WithoutFilter_ReturnsDefaultLimitInDescendingOrder()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            var result = await repo.GetAlarmHistoryAsync();

            Assert.Equal(10, result.Count);
            AssertTimestamps(result, ExpectedTimestamps(seed, _ => true, 10));
            AssertDescendingByTimestamp(result);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmHistoryAsync_TimeRangeFilter_ReturnsOnlyRecordsWithinBoundsInclusive()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            DateTime from = _baseTime.AddMinutes(8);
            DateTime to = _baseTime.AddDays(1).AddMinutes(2);
            var filter = new AlarmHistoryFilter { From = from, To = to };

            var result = await repo.GetAlarmHistoryAsync(filter);

            // 跨日范围: 第 0 天 8-9 分 + 第 1 天 0-2 分, 共 5 条, 两端边界值必须包含
            Assert.Equal(5, result.Count);
            AssertTimestamps(result, ExpectedTimestamps(seed, r => r.Timestamp >= from && r.Timestamp <= to, 10));
            Assert.Contains(result, r => r.Timestamp == from);
            Assert.Contains(result, r => r.Timestamp == to);
            AssertDescendingByTimestamp(result);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmHistoryAsync_DeviceIdFilter_MatchesCaseInsensitive()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            // 库内存的是 Dev-A, 用小写 dev-a 查询
            var filter = new AlarmHistoryFilter { DeviceId = "dev-a" };

            var result = await repo.GetAlarmHistoryAsync(filter);

            Assert.Equal(10, result.Count);
            AssertTimestamps(result, ExpectedTimestamps(seed, r => r.DeviceId == DeviceA, 10));
            Assert.All(result, r => Assert.Equal(DeviceA, r.DeviceId));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmHistoryAsync_AlarmCodeFilter_MatchesCaseInsensitive()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            // 库内存的是 ALM_LOW, 用小写 alm_low 查询
            var filter = new AlarmHistoryFilter { AlarmCode = "alm_low" };

            var result = await repo.GetAlarmHistoryAsync(filter);

            Assert.Equal(10, result.Count);
            AssertTimestamps(result, ExpectedTimestamps(seed, r => r.AlarmCode == AlarmLow, 10));
            Assert.All(result, r => Assert.Equal(AlarmLow, r.AlarmCode));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmHistoryAsync_FilterLimitOverridesDefault()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            var filter = new AlarmHistoryFilter { Limit = 5 };

            var result = await repo.GetAlarmHistoryAsync(filter);

            Assert.Equal(5, result.Count);
            AssertTimestamps(result, ExpectedTimestamps(seed, _ => true, 5));
            AssertDescendingByTimestamp(result);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmHistoryAsync_CombinedTimeAndDeviceFilter_ReturnsMatchingRecords()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            DateTime from = _baseTime.AddMinutes(2);
            DateTime to = _baseTime.AddDays(1).AddMinutes(3);
            // 库内存的是 Dev-B, 用大写 DEV-B 查询
            var filter = new AlarmHistoryFilter { From = from, To = to, DeviceId = "DEV-B" };

            var result = await repo.GetAlarmHistoryAsync(filter);

            // Dev-B(奇数分钟) 在范围内: 第 0 天 3/5/7/9 分 + 第 1 天 1/3 分, 共 6 条
            Assert.Equal(6, result.Count);
            AssertTimestamps(result, ExpectedTimestamps(seed,
                r => r.Timestamp >= from && r.Timestamp <= to && r.DeviceId == DeviceB, 10));
            Assert.All(result, r => Assert.Equal(DeviceB, r.DeviceId));
            Assert.All(result, r => Assert.InRange(r.Timestamp, from, to));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmHistoryAsync_NoMatchingRecords_ReturnsEmptyList()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            await SeedAsync(repo, BuildSeedRecords());

            var filter = new AlarmHistoryFilter { DeviceId = "dev-none" };

            var result = await repo.GetAlarmHistoryAsync(filter);

            Assert.Empty(result);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmsWithTelemetryContextAsync_OverlappingWindows_ReturnsOnlyOwnDeviceRows()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);

            // 两台设备报警相距 7 分钟, 默认 5 分钟窗口彼此重叠 3 分钟: 串台缺陷在此区域必然暴露
            var alarmA = new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh };
            var alarmB = new AlarmRecord { DeviceId = DeviceB, Timestamp = _baseTime.AddMinutes(7), AlarmCode = AlarmLow };
            await SeedAsync(repo, [alarmA, alarmB]);

            // 11:57-12:10 每 30 秒一行, 两台设备交替写入: 两边的窗口内都混有对方的行
            List<SensorReadingRecord> telemetry = [];
            for (int i = 0; i <= 26; i++)
            {
                telemetry.Add(BuildTelemetry(i % 2 == 0 ? DeviceA : DeviceB, _baseTime.AddMinutes(-3).AddSeconds(i * 30)));
            }
            await SeedTelemetryAsync(factory, telemetry);

            var result = await repo.GetAlarmsWithTelemetryContextAsync();

            Assert.Equal(2, result.Count);

            var contextA = Assert.Single(result, c => c.Alarm.DeviceId == DeviceA);
            AssertContextMatches(contextA, telemetry, DeviceA,
                _baseTime.AddMinutes(-5), _baseTime.AddMinutes(5));

            var contextB = Assert.Single(result, c => c.Alarm.DeviceId == DeviceB);
            AssertContextMatches(contextB, telemetry, DeviceB,
                _baseTime.AddMinutes(2), _baseTime.AddMinutes(12));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmsWithTelemetryContextAsync_WindowBoundaries_InclusiveWithinOneSecondExcluded()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            await SeedAsync(repo, [new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh }]);

            DateTime tsMinusWindow = _baseTime.AddMinutes(-5);
            DateTime tsPlusWindow = _baseTime.AddMinutes(5);
            await SeedTelemetryAsync(factory,
            [
                BuildTelemetry(DeviceA, tsMinusWindow.AddSeconds(-1)),  // 窗口外 1 秒, 必须排除
                BuildTelemetry(DeviceA, tsMinusWindow),                 // 恰好下边界, 必须包含
                BuildTelemetry(DeviceA, _baseTime),                     // 报警时刻本身
                BuildTelemetry(DeviceA, tsPlusWindow),                  // 恰好上边界, 必须包含
                BuildTelemetry(DeviceA, tsPlusWindow.AddSeconds(1))     // 窗口外 1 秒, 必须排除
            ]);

            var result = await repo.GetAlarmsWithTelemetryContextAsync();

            var context = Assert.Single(result);
            Assert.Equal(
                new[] { tsMinusWindow, _baseTime, tsPlusWindow },
                context.SurroundingTelemetry.Select(r => r.Timestamp));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmsWithTelemetryContextAsync_CustomWindow_TenMinutesTakeEffect()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            await SeedAsync(repo, [new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh }]);

            // 6 分钟行会被默认 5 分钟窗口排除, 出现在结果里即证明 10 分钟自定义窗口生效
            await SeedTelemetryAsync(factory,
            [
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(-10)),     // 恰好下边界, 包含
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(-6)),      // 默认窗口外/自定义窗口内
                BuildTelemetry(DeviceA, _baseTime),
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(10)),      // 恰好上边界, 包含
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(11))       // 自定义窗口外, 排除
            ]);

            var result = await repo.GetAlarmsWithTelemetryContextAsync(
                telemetryWindow: TimeSpan.FromMinutes(10));

            var context = Assert.Single(result);
            Assert.Equal(
                new[]
                {
                    _baseTime.AddMinutes(-10),
                    _baseTime.AddMinutes(-6),
                    _baseTime,
                    _baseTime.AddMinutes(10)
                },
                context.SurroundingTelemetry.Select(r => r.Timestamp));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmsWithTelemetryContextAsync_FilterLimit_ReturnsNewestFive()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            var filter = new AlarmHistoryFilter { Limit = 5 };

            var result = await repo.GetAlarmsWithTelemetryContextAsync(filter);

            Assert.Equal(5, result.Count);
            Assert.Equal(ExpectedTimestamps(seed, _ => true, 5), result.Select(c => c.Alarm.Timestamp));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmsWithTelemetryContextAsync_DeviceAndCodeFilter_MatchesCaseInsensitive()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var seed = BuildSeedRecords();
            await SeedAsync(repo, seed);

            // 库内存的是 Dev-A / ALM_LOW, 用小写查询验证过滤已下推且不区分大小写
            var filter = new AlarmHistoryFilter { DeviceId = "dev-a", AlarmCode = "alm_low" };

            var result = await repo.GetAlarmsWithTelemetryContextAsync(filter);

            var expected = ExpectedTimestamps(seed,
                r => r.DeviceId == DeviceA && r.AlarmCode == AlarmLow, 10);
            Assert.Equal(expected, result.Select(c => c.Alarm.Timestamp));
            Assert.All(result, c =>
            {
                Assert.Equal(DeviceA, c.Alarm.DeviceId);
                Assert.Equal(AlarmLow, c.Alarm.AlarmCode);
            });
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmsWithTelemetryContextAsync_AlarmWithoutTelemetry_ReturnsEmptyListNotThrow()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            await SeedAsync(repo, [new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh }]);

            var result = await repo.GetAlarmsWithTelemetryContextAsync();

            var context = Assert.Single(result);
            Assert.NotNull(context.SurroundingTelemetry);
            Assert.Empty(context.SurroundingTelemetry);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetAlarmsWithTelemetryContextAsync_MassiveOutOfWindowTelemetry_ReturnsOnlyInWindowRows()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            await SeedAsync(repo, [new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh }]);

            // 窗口内 5 行 + 10 天前起逐秒 2000 行窗口外数据: 退化成全表载入的实现会拉回全部 2005 行
            List<SensorReadingRecord> telemetry =
            [
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(-2)),
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(-1)),
                BuildTelemetry(DeviceA, _baseTime),
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(1)),
                BuildTelemetry(DeviceA, _baseTime.AddMinutes(2))
            ];
            for (int i = 0; i < 2000; i++)
                telemetry.Add(BuildTelemetry(DeviceA, _baseTime.AddDays(-10).AddSeconds(i)));

            await SeedTelemetryAsync(factory, telemetry);

            var result = await repo.GetAlarmsWithTelemetryContextAsync();

            var context = Assert.Single(result);
            Assert.Equal(5, context.SurroundingTelemetry.Count);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    #region 写入侧测试 (SaveAlarmRecordAsync / UpdateAlarmStatesAsync)

    /// <summary>SaveAlarmRecordAsync 返回自增 ID 且 > 0, 连续保存两条 ID 严格递增</summary>
    [Fact]
    public async Task SaveAlarmRecordAsync_连续保存两条_返回自增ID且严格递增()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);

            long firstId = await repo.SaveAlarmRecordAsync(new AlarmRecord
            {
                DeviceId = DeviceA,
                Timestamp = _baseTime,
                AlarmCode = AlarmHigh,
                TriggerValue = 1
            });
            long secondId = await repo.SaveAlarmRecordAsync(new AlarmRecord
            {
                DeviceId = DeviceB,
                Timestamp = _baseTime.AddMinutes(1),
                AlarmCode = AlarmLow,
                TriggerValue = 2
            });

            Assert.True(firstId > 0, $"首条自增 ID 应 > 0, 实际 {firstId}");
            Assert.Equal(firstId + 1, secondId);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    /// <summary>保存后按 Id 读回: DeviceId/AlarmCode/TriggerValue/QualityCode/State/Timestamp 与写入一致;
    /// Timestamp 列为 TEXT, 断言用整秒固定时间, 即现有表结构与查询接口能完整取回的精度</summary>
    [Fact]
    public async Task SaveAlarmRecordAsync_按Id读回_字段与写入一致()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            var written = new AlarmRecord
            {
                DeviceId = DeviceA,
                Timestamp = _baseTime.AddMinutes(3),
                AlarmCode = AlarmHigh,
                TriggerValue = 42.5,
                QualityCode = DataQuality.Uncertain,
                State = AlarmState.UNACK
            };

            long id = await repo.SaveAlarmRecordAsync(written);

            Assert.True(id > 0);
            var history = await repo.GetAlarmHistoryAsync();
            var read = Assert.Single(history, r => r.Id == id);

            Assert.Equal(written.DeviceId, read.DeviceId);
            Assert.Equal(written.Timestamp, read.Timestamp);
            Assert.Equal(written.AlarmCode, read.AlarmCode);
            Assert.Equal(written.TriggerValue, read.TriggerValue);
            Assert.Equal(written.QualityCode, read.QualityCode);
            Assert.Equal(written.State, read.State);
            // SaveAlarmRecordAsync 只写六列基础字段, 确认/恢复时间读回应为空
            Assert.Null(read.AcknowledgedAt);
            Assert.Null(read.ClearedAt);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    /// <summary>UpdateAlarmStatesAsync 单条 ACK: 状态与确认时间落库, 重新查询读回一致, 且不清空 ClearedAt 之外的列</summary>
    [Fact]
    public async Task UpdateAlarmStatesAsync_单条确认_状态与确认时间读回一致()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            long id = await repo.SaveAlarmRecordAsync(
                new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh, TriggerValue = 10 });

            DateTime acknowledgedAt = _baseTime.AddMinutes(1);
            await repo.UpdateAlarmStatesAsync(
                [new AlarmRecord { Id = id, State = AlarmState.ACK, AcknowledgedAt = acknowledgedAt }]);

            var read = Assert.Single(await repo.GetAlarmHistoryAsync(), r => r.Id == id);

            Assert.Equal(AlarmState.ACK, read.State);
            Assert.NotNull(read.AcknowledgedAt);
            Assert.Equal(acknowledgedAt, read.AcknowledgedAt);
            Assert.Null(read.ClearedAt);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    /// <summary>UpdateAlarmStatesAsync 批量三条(UNACK/ACK/NORMAL): 三条全部落到各自状态, 互不串行、互不清空;
    /// 更新对象只带 Id 与三个状态列, 顺带锁死 UPDATE 语句只改 State/AcknowledgedAt/ClearedAt 的契约</summary>
    [Fact]
    public async Task UpdateAlarmStatesAsync_批量三条_各自落到对应状态互不串扰()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            long idUnack = await repo.SaveAlarmRecordAsync(
                new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh, TriggerValue = 1 });
            long idAck = await repo.SaveAlarmRecordAsync(
                new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime.AddMinutes(1), AlarmCode = AlarmHigh, TriggerValue = 2 });
            long idNormal = await repo.SaveAlarmRecordAsync(
                new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime.AddMinutes(2), AlarmCode = AlarmHigh, TriggerValue = 3 });

            DateTime acknowledgedAt = _baseTime.AddMinutes(10);
            DateTime clearedAt = _baseTime.AddMinutes(11);
            await repo.UpdateAlarmStatesAsync(
            [
                new AlarmRecord { Id = idUnack, State = AlarmState.UNACK, AcknowledgedAt = null, ClearedAt = null },
                new AlarmRecord { Id = idAck, State = AlarmState.ACK, AcknowledgedAt = acknowledgedAt, ClearedAt = null },
                new AlarmRecord { Id = idNormal, State = AlarmState.NORMAL, AcknowledgedAt = acknowledgedAt, ClearedAt = clearedAt }
            ]);

            var history = await repo.GetAlarmHistoryAsync();

            var unack = Assert.Single(history, r => r.Id == idUnack);
            Assert.Equal(AlarmState.UNACK, unack.State);
            Assert.Null(unack.AcknowledgedAt);   // 其它行的确认时间不得串到本行
            Assert.Null(unack.ClearedAt);        // 其它行的恢复时间不得串到本行

            var ack = Assert.Single(history, r => r.Id == idAck);
            Assert.Equal(AlarmState.ACK, ack.State);
            Assert.Equal(acknowledgedAt, ack.AcknowledgedAt);
            Assert.Null(ack.ClearedAt);          // 确认操作不得顺手清空/填入恢复时间

            var normal = Assert.Single(history, r => r.Id == idNormal);
            Assert.Equal(AlarmState.NORMAL, normal.State);
            Assert.Equal(acknowledgedAt, normal.AcknowledgedAt); // 转入 NORMAL 不得清空已有确认时间
            Assert.Equal(clearedAt, normal.ClearedAt);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    /// <summary>3ab7619 的直接回归锁: 把记录更新为 NORMAL 且 ClearedAt 有值, 重新查询必须读到 State == NORMAL 且 ClearedAt != null,
    /// 缺陷形态是"日志已打印完全恢复 ACK→NORMAL, 库里那条仍停在 ACK 且 ClearedAt IS NULL"</summary>
    [Fact]
    public async Task UpdateAlarmStatesAsync_更新为NORMAL且ClearedAt有值_重新查询必须读到最终态()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            long id = await repo.SaveAlarmRecordAsync(
                new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh, TriggerValue = 10 });

            DateTime acknowledgedAt = _baseTime.AddMinutes(1);
            DateTime clearedAt = _baseTime.AddMinutes(2);
            await repo.UpdateAlarmStatesAsync(
                [new AlarmRecord { Id = id, State = AlarmState.NORMAL, AcknowledgedAt = acknowledgedAt, ClearedAt = clearedAt }]);

            var read = Assert.Single(await repo.GetAlarmHistoryAsync(), r => r.Id == id);

            Assert.Equal(AlarmState.NORMAL, read.State);
            Assert.NotNull(read.ClearedAt);
            Assert.Equal(clearedAt, read.ClearedAt);
            Assert.Equal(acknowledgedAt, read.AcknowledgedAt);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    /// <summary>更新不存在的记录(Id 不在库里的对象)不抛异常, 且库内已有行的状态不受影响</summary>
    [Fact]
    public async Task UpdateAlarmStatesAsync_更新不存在的记录_不抛异常且不影响其它行()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = await CreateRepositoryAsync(dbPath);
            long firstId = await repo.SaveAlarmRecordAsync(
                new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime, AlarmCode = AlarmHigh, TriggerValue = 1 });
            long secondId = await repo.SaveAlarmRecordAsync(
                new AlarmRecord { DeviceId = DeviceA, Timestamp = _baseTime.AddMinutes(1), AlarmCode = AlarmHigh, TriggerValue = 2 });

            DateTime touchedAt = _baseTime.AddMinutes(9);
            var exception = await Record.ExceptionAsync(() => repo.UpdateAlarmStatesAsync(
            [
                new AlarmRecord { Id = 0, State = AlarmState.ACK, AcknowledgedAt = touchedAt, ClearedAt = touchedAt },       // 未保存过的对象
                new AlarmRecord { Id = 9999, State = AlarmState.NORMAL, AcknowledgedAt = touchedAt, ClearedAt = touchedAt }  // 库里不存在的自增 ID
            ]));

            Assert.Null(exception);

            var history = await repo.GetAlarmHistoryAsync();
            var first = Assert.Single(history, r => r.Id == firstId);
            Assert.Equal(AlarmState.UNACK, first.State);
            Assert.Null(first.AcknowledgedAt);
            Assert.Null(first.ClearedAt);

            var second = Assert.Single(history, r => r.Id == secondId);
            Assert.Equal(AlarmState.UNACK, second.State);
            Assert.Null(second.AcknowledgedAt);
            Assert.Null(second.ClearedAt);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    #endregion 写入侧测试 (SaveAlarmRecordAsync / UpdateAlarmStatesAsync)

    /// <summary>按临时文件库构造仓储: in-memory 配置 + 建库, QueryLimit 显式设为 10。</summary>
    private static async Task<SqliteAlarmRepository> CreateRepositoryAsync(string dbPath)
    {
        var (repo, _) = await CreateFixtureAsync(dbPath);
        return repo;
    }

    /// <summary>构造仓储并返回工厂: 工厂供测试直接开连接批量写入遥测种子数据。</summary>
    private static async Task<(SqliteAlarmRepository Repo, SqliteConnectionFactory Factory)> CreateFixtureAsync(string dbPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DefaultConnection"] = $"Data Source={dbPath}",
                ["DatabaseSettings:QueryLimit"] = "10"
            })
            .Build();

        var factory = new SqliteConnectionFactory(config);
        await factory.InitializeDatabaseAsync();

        return (new SqliteAlarmRepository(factory, config), factory);
    }

    /// <summary>构造 30 条种子数据: 时间戳从固定基准时间起分散在 3 天内, 2 个设备 × 2 个报警编码。</summary>
    private static List<AlarmRecord> BuildSeedRecords()
    {
        var records = new List<AlarmRecord>();

        for (int i = 0; i < 30; i++)
        {
            records.Add(new AlarmRecord
            {
                DeviceId = i % 2 == 0 ? DeviceA : DeviceB,
                Timestamp = _baseTime.AddDays(i / 10).AddMinutes(i % 10),
                AlarmCode = (i / 2) % 2 == 0 ? AlarmHigh : AlarmLow,
                TriggerValue = i
            });
        }

        return records;
    }

    private static async Task SeedAsync(SqliteAlarmRepository repo, IEnumerable<AlarmRecord> records)
    {
        foreach (var record in records)
            await repo.SaveAlarmRecordAsync(record);
    }

    /// <summary>构造遥测行: 值字段仅作占位, 测试只关心设备与时间戳。</summary>
    private static SensorReadingRecord BuildTelemetry(string deviceId, DateTime timestamp) => new()
    {
        DeviceId = deviceId,
        Timestamp = timestamp,
        Temperature = Temperature.FromCelsius(25),
        Humidity = Humidity.FromPercent(60),
        StatusCode = DeviceStatus.Online,
        ErrorCode = ErrorCode.NoError,
        QualityCode = DataQuality.Good
    };

    /// <summary>绕过 SqliteTelemetryRepository 的 Channel 异步刷盘, 直接批量插入保证测试确定性。</summary>
    private static async Task SeedTelemetryAsync(SqliteConnectionFactory factory, IReadOnlyList<SensorReadingRecord> readings)
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync("""
            INSERT INTO TelemetryHistory (DeviceId, Timestamp, Temperature, Humidity, StatusCode, ErrorCode, QualityCode)
            VALUES (@DeviceId, @Timestamp, @Temperature, @Humidity, @StatusCode, @ErrorCode, @QualityCode)
            """, readings, transaction: transaction);

        await transaction.CommitAsync();
    }

    /// <summary>断言上下文遥测: 只含本设备、全部落在 [start, end] 内、行数与内容同种子数据推算结果一致且升序。</summary>
    private static void AssertContextMatches(
        AlarmWithTelemetry actual, List<SensorReadingRecord> telemetry, string deviceId,
        DateTime start, DateTime end)
    {
        Assert.NotNull(actual.SurroundingTelemetry);
        Assert.All(actual.SurroundingTelemetry, r => Assert.Equal(deviceId, r.DeviceId));
        Assert.All(actual.SurroundingTelemetry, r => Assert.InRange(r.Timestamp, start, end));

        Assert.Equal(
            telemetry.Where(r => r.DeviceId == deviceId && r.Timestamp >= start && r.Timestamp <= end)
                .OrderBy(r => r.Timestamp)
                .Select(r => r.Timestamp),
            actual.SurroundingTelemetry.Select(r => r.Timestamp));
    }

    /// <summary>计算种子数据中满足条件的记录按时间倒序、截断 limit 后应返回的时间戳序列。</summary>
    private static IEnumerable<DateTime> ExpectedTimestamps(
        List<AlarmRecord> seed, Func<AlarmRecord, bool> predicate, int limit)
        => seed.Where(predicate)
            .OrderByDescending(r => r.Timestamp)
            .Take(limit)
            .Select(r => r.Timestamp);

    private static void AssertTimestamps(List<AlarmRecord> actual, IEnumerable<DateTime> expected)
        => Assert.Equal(expected, actual.Select(r => r.Timestamp));

    private static void AssertDescendingByTimestamp(List<AlarmRecord> records)
    {
        for (int i = 1; i < records.Count; i++)
            Assert.True(records[i - 1].Timestamp > records[i].Timestamp,
                $"结果未按 Timestamp 倒序: 第 {i - 1} 条 {records[i - 1].Timestamp:O} 应晚于第 {i} 条 {records[i].Timestamp:O}");
    }

    private static void DeleteDatabaseFiles(string dbPath)
    {
        // Microsoft.Data.Sqlite 默认连接池会保留已打开的库文件句柄, 须先清池再删除
        SqliteConnection.ClearAllPools();

        // 连接均已 dispose, 文件锁应已释放; 兜底重试避免偶发句柄延迟导致残留临时文件
        foreach (string file in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            for (int attempt = 0; attempt < 3 && File.Exists(file); attempt++)
            {
                try { File.Delete(file); }
                catch (IOException) when (attempt < 2) { Thread.Sleep(100); }
            }
        }
    }
}
