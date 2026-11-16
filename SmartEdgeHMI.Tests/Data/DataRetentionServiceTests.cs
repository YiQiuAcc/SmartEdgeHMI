using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Retention;

namespace SmartEdgeHMI.Tests.Data;

public class DataRetentionServiceTests
{
    // 固定基准时间: PurgeAsync 的时间基准完全由参数决定, 不依赖系统时钟(与同目录现有测试约定一致)
    private static readonly DateTime _utcNow = new(2026, 1, 10, 12, 0, 0);

    [Fact]
    public async Task PurgeAsync_RetentionDays30_DeletesOnlyRowsBeyondRetention()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 30, alarmRetentionDays: 0);
            try
            {
                DateTime expired = _utcNow.AddDays(-40);
                DateTime keptNear = _utcNow.AddDays(-29);
                await SeedTelemetryAsync(factory, [BuildTelemetry(expired), BuildTelemetry(keptNear), BuildTelemetry(_utcNow)]);

                var result = await service.PurgeAsync(_utcNow, CancellationToken.None);

                Assert.Equal(1, result.TelemetryDeleted);
                Assert.Equal(0, result.AlarmDeleted);
                Assert.False(result.BatchLimitReached);
                Assert.Equal([keptNear, _utcNow], await QueryTimestampsAsync(factory, "TelemetryHistory"));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task PurgeAsync_RowExactlyAtCutoff_IsKept()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 30, alarmRetentionDays: 0);
            try
            {
                // 截止时间 = utcNow - 30 天; 恰好等于截止时间的行必须保留(严格小于才删)
                DateTime cutoff = _utcNow.AddDays(-30);
                await SeedTelemetryAsync(factory,
                [
                    BuildTelemetry(cutoff.AddSeconds(-1)),  // 截止前 1 秒, 必须删除
                    BuildTelemetry(cutoff),                 // 恰好等于截止时间, 必须保留
                    BuildTelemetry(cutoff.AddSeconds(1))    // 截止后 1 秒, 必须保留
                ]);

                var result = await service.PurgeAsync(_utcNow, CancellationToken.None);

                Assert.Equal(1, result.TelemetryDeleted);
                Assert.Equal([cutoff, cutoff.AddSeconds(1)], await QueryTimestampsAsync(factory, "TelemetryHistory"));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task PurgeAsync_TwelveThousandExpiredRows_AllDeletedAcrossBatches()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 30, alarmRetentionDays: 0);
            try
            {
                // 12,000 条过期行(批大小 5000, 需 3 个有效批次): 一次 PurgeAsync 后全部删除,
                // 证明批循环生效而不是只删第一批
                List<SensorReadingRecord> seed = [];
                for (int i = 0; i < 12_000; i++)
                    seed.Add(BuildTelemetry(_utcNow.AddDays(-40).AddSeconds(i)));

                await SeedTelemetryAsync(factory, seed);

                var result = await service.PurgeAsync(_utcNow, CancellationToken.None);

                Assert.Equal(12_000, result.TelemetryDeleted);
                Assert.False(result.BatchLimitReached);
                Assert.Equal(0, await CountRowsAsync(factory, "TelemetryHistory"));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task PurgeAsync_RetentionDisabled_DeletesNothing()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 0, alarmRetentionDays: 0);
            try
            {
                await SeedTelemetryAsync(factory, [BuildTelemetry(_utcNow.AddDays(-40))]);

                var result = await service.PurgeAsync(_utcNow, CancellationToken.None);

                // RetentionDays <= 0 表示关闭清理, 过期行也必须原样保留
                Assert.Equal(0, result.TelemetryDeleted);
                Assert.Equal(0, result.AlarmDeleted);
                Assert.Equal(1, await CountRowsAsync(factory, "TelemetryHistory"));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task PurgeAsync_AlarmRetention30_DeletesExpiredKeepsRecent()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 0, alarmRetentionDays: 30);
            try
            {
                DateTime expired = _utcNow.AddDays(-40);
                DateTime keptNear = _utcNow.AddDays(-29);
                await SeedAlarmsAsync(factory, [BuildAlarm(expired), BuildAlarm(keptNear), BuildAlarm(_utcNow)]);

                var result = await service.PurgeAsync(_utcNow, CancellationToken.None);

                Assert.Equal(1, result.AlarmDeleted);
                Assert.Equal(0, result.TelemetryDeleted);
                Assert.Equal([keptNear, _utcNow], await QueryTimestampsAsync(factory, "AlarmHistory"));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task PurgeAsync_AlarmRetentionDisabled_DeletesNoAlarm()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            // AlarmRetentionDays 默认语义: 报警属于审计记录, <= 0 时不删
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 0, alarmRetentionDays: 0);
            try
            {
                await SeedAlarmsAsync(factory, [BuildAlarm(_utcNow.AddDays(-40))]);

                var result = await service.PurgeAsync(_utcNow, CancellationToken.None);

                Assert.Equal(0, result.AlarmDeleted);
                Assert.Equal(1, await CountRowsAsync(factory, "AlarmHistory"));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task PurgeAsync_SecondRunIsIdempotent_ReturnsZeroDeleted()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 30, alarmRetentionDays: 30);
            try
            {
                await SeedTelemetryAsync(factory, [BuildTelemetry(_utcNow.AddDays(-40))]);
                await SeedAlarmsAsync(factory, [BuildAlarm(_utcNow.AddDays(-40))]);

                var first = await service.PurgeAsync(_utcNow, CancellationToken.None);
                var second = await service.PurgeAsync(_utcNow, CancellationToken.None);

                Assert.Equal(1, first.TelemetryDeleted);
                Assert.Equal(1, first.AlarmDeleted);
                Assert.Equal(0, second.TelemetryDeleted);
                Assert.Equal(0, second.AlarmDeleted);
                Assert.False(second.BatchLimitReached);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task PurgeAsync_NonExpiredTelemetryAndAlarm_StillQueryableAfterPurge()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SmartEdgeHMI_Test_{Guid.NewGuid():N}.db");

        try
        {
            var (factory, service) = await CreateFixtureAsync(dbPath, retentionDays: 30, alarmRetentionDays: 30);
            try
            {
                await SeedTelemetryAsync(factory,
                    [BuildTelemetry(_utcNow.AddDays(-40)), BuildTelemetry(_utcNow.AddDays(-29)), BuildTelemetry(_utcNow)]);
                await SeedAlarmsAsync(factory,
                    [BuildAlarm(_utcNow.AddDays(-40)), BuildAlarm(_utcNow.AddDays(-29)), BuildAlarm(_utcNow)]);

                await service.PurgeAsync(_utcNow, CancellationToken.None);

                // 清理只影响过期行: 未过期的遥测与报警必须仍然可查
                Assert.Equal([_utcNow.AddDays(-29), _utcNow], await QueryTimestampsAsync(factory, "TelemetryHistory"));
                Assert.Equal([_utcNow.AddDays(-29), _utcNow], await QueryTimestampsAsync(factory, "AlarmHistory"));
            }
            finally
            {
                await service.DisposeAsync();
            }
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    /// <summary>按临时文件库构造保留服务: in-memory 配置 + 建库; 保留天数由测试显式指定。</summary>
    private static async Task<(SqliteConnectionFactory Factory, DataRetentionService Service)> CreateFixtureAsync(
        string dbPath, int retentionDays, int alarmRetentionDays)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DefaultConnection"] = $"Data Source={dbPath}",
                ["DatabaseSettings:RetentionDays"] = retentionDays.ToString(),
                ["DatabaseSettings:AlarmRetentionDays"] = alarmRetentionDays.ToString()
            })
            .Build();

        var factory = new SqliteConnectionFactory(config);
        await factory.InitializeDatabaseAsync();

        return (factory, new DataRetentionService(factory, config));
    }

    /// <summary>构造遥测行: 值字段仅作占位, 测试只关心时间戳是否过期。</summary>
    private static SensorReadingRecord BuildTelemetry(DateTime timestamp) => new()
    {
        DeviceId = "TEST-01",
        Timestamp = timestamp,
        Temperature = Temperature.FromCelsius(25),
        Humidity = Humidity.FromPercent(60),
        StatusCode = DeviceStatus.Online,
        ErrorCode = ErrorCode.NoError,
        QualityCode = DataQuality.Good
    };

    /// <summary>构造报警行: 值字段仅作占位, 测试只关心时间戳是否过期。</summary>
    private static AlarmRecord BuildAlarm(DateTime timestamp) => new()
    {
        DeviceId = "TEST-01",
        Timestamp = timestamp,
        AlarmCode = "ALM_TEST",
        TriggerValue = 75
    };

    /// <summary>绕过业务仓储直接连库按 500 行/批(单事务)插入遥测种子数据, 保证确定性。</summary>
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

    /// <summary>绕过业务仓储直接连库插入报警种子数据, 保证确定性。</summary>
    private static async Task SeedAlarmsAsync(SqliteConnectionFactory factory, IReadOnlyList<AlarmRecord> records)
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync("""
            INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode, TriggerValue, QualityCode, State)
            VALUES (@DeviceId, @Timestamp, @AlarmCode, @TriggerValue, @QualityCode, @State)
            """, records, transaction: transaction);

        await transaction.CommitAsync();
    }

    private static async Task<long> CountRowsAsync(SqliteConnectionFactory factory, string tableName)
    {
        await using var connection = await factory.CreateConnectionAsync();
        return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {tableName}");
    }

    /// <summary>读取表内全部 Timestamp(按时间升序): 表名来自测试内的固定调用点, 非外部输入。</summary>
    private static async Task<List<DateTime>> QueryTimestampsAsync(SqliteConnectionFactory factory, string tableName)
    {
        await using var connection = await factory.CreateConnectionAsync();
        var raw = await connection.QueryAsync<string>($"SELECT Timestamp FROM {tableName} ORDER BY Timestamp ASC");
        return raw.Select(s => DateTime.Parse(s, CultureInfo.InvariantCulture)).ToList();
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
