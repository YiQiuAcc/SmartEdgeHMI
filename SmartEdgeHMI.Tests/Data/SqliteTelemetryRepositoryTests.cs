using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Repositories;

namespace SmartEdgeHMI.Tests.Data;

public class SqliteTelemetryRepositoryTests
{
    [Fact]
    public async Task SaveTelemetryAsync_FlushesWithinOneSecondWindow()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DatabaseSettings:DefaultConnection"] = $"Data Source={dbPath}"
                })
                .Build();

            var factory = new SqliteConnectionFactory(config);
            await factory.InitializeDatabaseAsync();

            var repo = new SqliteTelemetryRepository(factory);
            try
            {
                await repo.SaveTelemetryAsync(new SensorReadingRecord
                {
                    DeviceId = "TEST-01",
                    Timestamp = DateTime.Now,
                    Temperature = Temperature.FromCelsius(25.5),
                    Humidity = Humidity.FromPercent(60),
                    StatusCode = DeviceStatus.Online,
                    ErrorCode = ErrorCode.NoError,
                    QualityCode = DataQuality.Good
                });

                // FlushIntervalMs=1000 (50 条或 1 秒窗口), 单条数据只可能由时间窗触发刷盘;
                // 3 秒是放宽的稳定性上界: 消费循环最坏情况需等满两个超时周期才比较时间窗
                long count = await WaitForRowCountAsync(factory, TimeSpan.FromSeconds(3));

                Assert.Equal(1, count);
            }
            finally
            {
                await repo.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    // 轮询 TelemetryHistory 行数; 限时内没有数据则返回 0
    private static async Task<long> WaitForRowCountAsync(SqliteConnectionFactory factory, TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < limit)
        {
            await using SqliteConnection connection = await factory.CreateConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM TelemetryHistory";

            long count = Convert.ToInt64(await command.ExecuteScalarAsync());
            if (count > 0)
                return count;

            await Task.Delay(100);
        }

        return 0;
    }

    // 固定基准时间, 不使用 DateTime.Now: 避免测试撞上真实时钟的日期/小时边界(与报警仓储测试约定一致)
    private static readonly DateTime _baseTime = new(2026, 1, 10, 12, 0, 0);

    [Fact]
    public async Task GetTelemetryHistoryAsync_FewerRowsThanTargetPoints_ReturnsAllRowsInTimeOrder()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            try
            {
                // 50 行按 1 秒间隔, targetPoints=1000: 数据量少于目标点数, 保持旧行为返回全部原始行
                List<SensorReadingRecord> seed = [];
                for (int i = 0; i < 50; i++)
                    seed.Add(BuildTelemetry(_baseTime.AddSeconds(i), 20 + i));

                await SeedTelemetryAsync(factory, seed);

                var result = await repo.GetTelemetryHistoryAsync(_baseTime, _baseTime.AddSeconds(49), 1000);

                Assert.Equal(50, result.Count);
                Assert.Equal(seed.Select(r => r.Timestamp), result.Select(r => r.Timestamp));
            }
            finally
            {
                await repo.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetTelemetryHistoryAsync_BucketedSampling_ReturnsWithinTargetAndKeepsBucketMinMax()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            try
            {
                await SeedTelemetryAsync(factory, BuildBucketedDataset());

                var result = await repo.GetTelemetryHistoryAsync(_baseTime, _baseTime.AddSeconds(10), 100);

                Assert.NotEmpty(result);
                Assert.True(result.Count <= 100, $"返回条数 {result.Count} 超过目标点数 100");

                for (int i = 1; i < result.Count; i++)
                    Assert.True(result[i].Timestamp > result[i - 1].Timestamp,
                        $"结果未按时间严格递增: 第 {i - 1} 条 {result[i - 1].Timestamp:O} 不早于第 {i} 条 {result[i].Timestamp:O}");

                // 桶 k 的最低温 k*100 与最高温 k*100+99 必须都出现在结果中(尖峰不被抹平)
                for (int k = 0; k < 10; k++)
                {
                    Assert.Contains(result, r => r.Temperature.Celsius == k * 100);
                    Assert.Contains(result, r => r.Temperature.Celsius == k * 100 + 99);
                }
            }
            finally
            {
                await repo.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetTelemetryHistoryAsync_ReturnedPoints_AllExistInOriginalData()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            try
            {
                List<SensorReadingRecord> seed = BuildBucketedDataset();
                await SeedTelemetryAsync(factory, seed);

                var result = await repo.GetTelemetryHistoryAsync(_baseTime, _baseTime.AddSeconds(10), 100);

                Assert.NotEmpty(result);

                // 每个返回点(时间戳+温度)都能在原始数据中找到, 证明没有伪造点
                var known = seed.Select(r => (r.Timestamp, r.Temperature.Celsius)).ToHashSet();
                Assert.All(result, r => Assert.True(
                    known.Contains((r.Timestamp, r.Temperature.Celsius)),
                    $"点 {r.Timestamp:O} / {r.Temperature.Celsius}°C 不在原始数据中, 疑似伪造点"));
            }
            finally
            {
                await repo.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetTelemetryHistoryAsync_InvalidRangeOrTargetPoints_ReturnsEmptyOrRawRows()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            try
            {
                List<SensorReadingRecord> seed = [];
                for (int i = 0; i < 120; i++)
                    seed.Add(BuildTelemetry(_baseTime.AddSeconds(i), 20 + i));

                await SeedTelemetryAsync(factory, seed);

                // to < from 与 to == from 都返回空列表
                Assert.Empty(await repo.GetTelemetryHistoryAsync(_baseTime.AddSeconds(10), _baseTime, 1000));
                Assert.Empty(await repo.GetTelemetryHistoryAsync(_baseTime, _baseTime, 1000));

                // targetPoints=2 (< 3) 退化为原始查询: 返回范围内全部行
                var result = await repo.GetTelemetryHistoryAsync(_baseTime, _baseTime.AddSeconds(119), 2);

                Assert.Equal(120, result.Count);
                Assert.Equal(seed.Select(r => r.Timestamp), result.Select(r => r.Timestamp));
            }
            finally
            {
                await repo.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetTelemetryHistoryAsync_TimeBoundaries_FromAndToRowsIncluded()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (repo, factory) = await CreateFixtureAsync(dbPath);
            try
            {
                DateTime from = _baseTime;
                DateTime to = _baseTime.AddSeconds(3);

                // 边界外 1 秒的行必须排除, from/to 上的行必须包含; 每行温度可区分
                await SeedTelemetryAsync(factory,
                [
                    BuildTelemetry(from.AddSeconds(-1), 21),
                    BuildTelemetry(from, 22),
                    BuildTelemetry(from.AddSeconds(1), 23),
                    BuildTelemetry(to, 24),
                    BuildTelemetry(to.AddSeconds(1), 25)
                ]);

                var result = await repo.GetTelemetryHistoryAsync(from, to, 1000);

                Assert.Equal(new[] { from, from.AddSeconds(1), to }, result.Select(r => r.Timestamp));
            }
            finally
            {
                await repo.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    // 临时文件库 + in-memory 配置; 工厂一并返回, 供测试直接建立连接写入种子数据
    private static async Task<(SqliteTelemetryRepository Repo, SqliteConnectionFactory Factory)> CreateFixtureAsync(string dbPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DefaultConnection"] = $"Data Source={dbPath}"
            })
            .Build();

        var factory = new SqliteConnectionFactory(config);
        await factory.InitializeDatabaseAsync();

        return (new SqliteTelemetryRepository(factory), factory);
    }

    // 10 桶 × 每桶 100 行, 桶宽 1 秒(总跨度 10 秒 / targetPoints=100);
    // 行间隔 10ms 且整体偏移 5ms, 让每行远离整秒桶边界, 规避 julianday 的浮点量化误差;
    // 桶 k 的第 i 行温度 = k*100 + i, 因此每桶的最高温与最低温都可区分
    private static List<SensorReadingRecord> BuildBucketedDataset()
    {
        var readings = new List<SensorReadingRecord>(1000);

        for (int k = 0; k < 10; k++)
        {
            for (int i = 0; i < 100; i++)
                readings.Add(BuildTelemetry(_baseTime.AddMilliseconds(k * 1000 + i * 10 + 5), k * 100 + i));
        }

        return readings;
    }

    // 只关心时间戳与温度, 其余字段用占位值
    private static SensorReadingRecord BuildTelemetry(DateTime timestamp, double temperatureCelsius) => new()
    {
        DeviceId = "TEST-01",
        Timestamp = timestamp,
        Temperature = Temperature.FromCelsius(temperatureCelsius),
        Humidity = Humidity.FromPercent(60),
        StatusCode = DeviceStatus.Online,
        ErrorCode = ErrorCode.NoError,
        QualityCode = DataQuality.Good
    };

    // 绕过 Channel 异步刷盘直接写入数据库, 否则种子数据的可见时间不确定; 500 行/批共用单事务
    private static async Task SeedTelemetryAsync(SqliteConnectionFactory factory, IReadOnlyList<SensorReadingRecord> readings)
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const int batchSize = 500;
        for (int offset = 0; offset < readings.Count; offset += batchSize)
        {
            await connection.ExecuteAsync("""
                INSERT INTO TelemetryHistory (DeviceId, Timestamp, Temperature, Humidity, StatusCode, ErrorCode, QualityCode)
                VALUES (@DeviceId, @Timestamp, @Temperature, @Humidity, @StatusCode, @ErrorCode, @QualityCode)
                """, readings.Skip(offset).Take(batchSize), transaction: transaction);
        }

        await transaction.CommitAsync();
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
