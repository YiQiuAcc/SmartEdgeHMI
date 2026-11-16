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
