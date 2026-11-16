using System.Diagnostics;
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

                // FlushIntervalMs=1000 (50 条或 1 秒窗口); 单条数据只可能由时间窗触发刷盘。
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

    /// <summary>轮询 TelemetryHistory 行数, 限时内未出现数据返回 0。</summary>
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
