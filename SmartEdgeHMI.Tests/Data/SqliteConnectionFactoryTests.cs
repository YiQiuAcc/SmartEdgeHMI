using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Data;

namespace SmartEdgeHMI.Tests.Data;

public class SqliteConnectionFactoryTests
{
    [Fact]
    public async Task CreateConnectionAsync_AppliesNormalSynchronousAndWal()
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

            // synchronous/temp_store 是连接级设置, 经 CreateConnectionAsync 新建的连接
            // 必须同样是 NORMAL (1), 否则实际运行在 SQLite 默认 FULL (2) 口径
            await using SqliteConnection connection = await factory.CreateConnectionAsync();

            long synchronous = Convert.ToInt64(await ExecutePragmaAsync(connection, "PRAGMA synchronous"));
            string journalMode = (await ExecutePragmaAsync(connection, "PRAGMA journal_mode"))!.ToString()!;

            Assert.Equal(1, synchronous);
            Assert.Equal("wal", journalMode);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task InitializeDatabaseAsync_CreatesDeviceTimestampCompositeIndex()
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

            await using SqliteConnection connection = await factory.CreateConnectionAsync();

            // 报警上下文窗口查询 (DeviceId = ? AND Timestamp BETWEEN) 依赖 (DeviceId, Timestamp) 复合索引
            var indexNames = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='TelemetryHistory'";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                indexNames.Add(reader.GetString(0));

            Assert.Contains("idx_telemetry_device_timestamp", indexNames);

            // 窗口查询计划应 SEARCH 复合索引且不再需要 ORDER BY 临时 B 树
            await using var planCommand = connection.CreateCommand();
            planCommand.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT * FROM TelemetryHistory
                WHERE DeviceId = 'SIM-A' AND Timestamp >= '2026-01-01' AND Timestamp <= '2026-01-02'
                """;
            var planDetails = new List<string>();
            await using var planReader = await planCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await planReader.ReadAsync(TestContext.Current.CancellationToken))
                planDetails.Add(planReader.GetString(3));

            Assert.Contains(planDetails, detail => detail.Contains("USING INDEX idx_telemetry_device_timestamp"));
            Assert.DoesNotContain(planDetails, detail => detail.Contains("TEMP B-TREE"));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    private static async Task<object?> ExecutePragmaAsync(SqliteConnection connection, string pragma)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = pragma;
        return await command.ExecuteScalarAsync();
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
