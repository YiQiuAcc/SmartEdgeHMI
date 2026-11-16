using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Filters;
using SmartEdgeHMI.Data.Repositories;

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

    /// <summary>按临时文件库构造仓储: in-memory 配置 + 建库, QueryLimit 显式设为 10。</summary>
    private static async Task<SqliteAlarmRepository> CreateRepositoryAsync(string dbPath)
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

        return new SqliteAlarmRepository(factory, config);
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
