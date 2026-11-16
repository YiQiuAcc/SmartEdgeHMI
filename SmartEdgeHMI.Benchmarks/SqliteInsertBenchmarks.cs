using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace SmartEdgeHMI.Benchmarks;

/// <summary>
/// SQLite 遥测写入基准: 单条提交 vs 批量事务。
/// 被测范围 = 打开连接 → PRAGMA → 按批次开启事务 → 参数化插入 → 提交;
/// 数据准备与清表在 GlobalSetup / IterationSetup 中完成, 不计入耗时。
/// </summary>
[MemoryDiagnoser]
public class SqliteInsertBenchmarks
{
    private const int TotalRows = 10_000;
    private const string InsertSql = """
        INSERT INTO TelemetryHistory (DeviceId, Timestamp, Temperature, Humidity, StatusCode, ErrorCode, QualityCode)
        VALUES (@DeviceId, @Timestamp, @Temperature, @Humidity, @StatusCode, @ErrorCode, @QualityCode)
        """;
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS TelemetryHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DeviceId TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            Temperature REAL NOT NULL,
            Humidity REAL NOT NULL,
            StatusCode INTEGER NOT NULL,
            ErrorCode INTEGER NOT NULL,
            QualityCode INTEGER NOT NULL
        )
        """;

    private static readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "SmartEdgeHMI_Benchmarks");
    private static readonly string _dbPath = Path.Combine(_dbDirectory, "telemetry_bench.db");

    private string _connectionString = null!;
    private PreformattedRow[] _rows = [];

    /// <summary>每事务批量行数 (1 = 单条提交, 与批量事务对比)</summary>
    [Params(1, 50, 500)]
    public int BatchSize;

    [GlobalSetup]
    public void Setup()
    {
        // 预格式化 10,000 行参数化数据 (时间戳按 Microsoft.Data.Sqlite 的 TEXT 格式预转换, 字符串化不计入被测耗时)
        var rng = new Random(42);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double temperature = 25, humidity = 50;
        _rows = new PreformattedRow[TotalRows];

        for (int i = 0; i < TotalRows; i++)
        {
            temperature = Math.Clamp(temperature + (rng.NextDouble() - 0.5) * 0.5, 15, 35);
            humidity = Math.Clamp(humidity + (rng.NextDouble() - 0.5) * 0.8, 20, 80);
            _rows[i] = new PreformattedRow(
                "SIM-001",
                start.AddMilliseconds(i * 100L).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF"),
                Math.Round(temperature, 2),
                Math.Round(humidity, 2),
                1, 0, 0);
        }

        // 建库建表: 表结构与索引和 SqliteConnectionFactory.InitializeDatabaseAsync 保持一致
        Directory.CreateDirectory(_dbDirectory);
        string[] suffixes = ["", "-wal", "-shm"];
        foreach (string suffix in suffixes)
        {
            string file = _dbPath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }

        _connectionString = $"Data Source={_dbPath}";
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
        ExecuteNonQuery(connection, CreateSql);
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_telemetry_timestamp ON TelemetryHistory(Timestamp)");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_telemetry_device ON TelemetryHistory(DeviceId)");
    }

    [IterationSetup]
    public void Cleanup()
    {
        // 每轮前清空表, 保证三种批量参数插入的都是空表, 结果可比
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ExecuteNonQuery(connection, "DELETE FROM TelemetryHistory");
    }

    /// <summary>每轮写入固定 10,000 行, 按 BatchSize 分组提交事务</summary>
    [Benchmark]
    public async Task WriteTelemetryHistory_10000Rows()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        // synchronous 为连接级设置, 新连接需重新声明 WAL + NORMAL
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";
            await pragma.ExecuteNonQueryAsync();
        }

        for (int offset = 0; offset < _rows.Length; offset += BatchSize)
        {
            int count = Math.Min(BatchSize, _rows.Length - offset);

            await using var transaction = await connection.BeginTransactionAsync();
            using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = InsertSql;

            var parameters = new[]
            {
                new SqliteParameter("@DeviceId", null),
                new SqliteParameter("@Timestamp", null),
                new SqliteParameter("@Temperature", null),
                new SqliteParameter("@Humidity", null),
                new SqliteParameter("@StatusCode", null),
                new SqliteParameter("@ErrorCode", null),
                new SqliteParameter("@QualityCode", null),
            };
            command.Parameters.AddRange(parameters);

            for (int i = offset; i < offset + count; i++)
            {
                parameters[0].Value = _rows[i].DeviceId;
                parameters[1].Value = _rows[i].Timestamp;
                parameters[2].Value = _rows[i].Temperature;
                parameters[3].Value = _rows[i].Humidity;
                parameters[4].Value = _rows[i].StatusCode;
                parameters[5].Value = _rows[i].ErrorCode;
                parameters[6].Value = _rows[i].QualityCode;
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    private readonly record struct PreformattedRow(
        string DeviceId, string Timestamp, double Temperature, double Humidity,
        int StatusCode, int ErrorCode, int QualityCode);
}
