using System.IO;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Data;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Filters;
using SmartEdgeHMI.Data.Repositories;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Utils.Math;

namespace SmartEdgeHMI.Benchmarks;

/// <summary>查询基准: 三组"旧实现 vs 新实现"对照(报警历史 / 报警上下文 / 趋势降采样)</summary>
/// <remarks>
/// Legacy_* 是为对比保留的旧实现基线, 内联改写前的代码可独立运行, 不是生产代码;
/// New_* 调用当前仓储方法, 与生产路径完全一致(含连接工厂的 PRAGMA 声明)。
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class QueryBenchmarks
{
    private const int TelemetryRows = 200_000;
    private const int AlarmRows = 5_000;
    private const int TrendTargetPoints = 1000;
    private const int AlarmHistoryLimit = 50;
    private const int AlarmContextLimit = 20;
    private const int SeedBatchSize = 500;

    private const string TelemetryInsertSql = """
        INSERT INTO TelemetryHistory (DeviceId, Timestamp, Temperature, Humidity, StatusCode, ErrorCode, QualityCode)
        VALUES (@DeviceId, @Timestamp, @Temperature, @Humidity, @StatusCode, @ErrorCode, @QualityCode)
        """;
    private const string AlarmInsertSql = """
        INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode, TriggerValue, QualityCode, State)
        VALUES (@DeviceId, @Timestamp, @AlarmCode, @TriggerValue, @QualityCode, @State)
        """;
    private const string CreateTelemetrySql = """
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
    private const string CreateAlarmSql = """
        CREATE TABLE IF NOT EXISTS AlarmHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DeviceId TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            AlarmCode TEXT NOT NULL,
            TriggerValue REAL NOT NULL DEFAULT 0,
            QualityCode INTEGER NOT NULL DEFAULT 0,
            State INTEGER NOT NULL DEFAULT 0,
            AcknowledgedAt TEXT,
            ClearedAt TEXT
        )
        """;

    private static readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "SmartEdgeHMI_Benchmarks");
    private static readonly string _dbPath = Path.Combine(_dbDirectory, "query_bench.db");

    private string _connectionString = null!;
    private DateTime _end;
    private SqliteConnectionFactory _factory = null!;
    private SqliteAlarmRepository _alarmRepo = null!;
    private SqliteTelemetryRepository _telemetryRepo = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 统一时间基准: 数据均匀分布在最近 30 天, 三组对照的查询窗口都由此推导
        _end = DateTime.Now;

        // 建库建表: 表结构与索引和 SqliteConnectionFactory.InitializeDatabaseAsync 保持一致
        Directory.CreateDirectory(_dbDirectory);
        SqliteConnection.ClearAllPools();
        DeleteDatabaseFiles();

        _connectionString = $"Data Source={_dbPath}";
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
        ExecuteNonQuery(connection, CreateTelemetrySql);
        ExecuteNonQuery(connection, CreateAlarmSql);
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_telemetry_timestamp ON TelemetryHistory(Timestamp)");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_telemetry_device ON TelemetryHistory(DeviceId)");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_alarm_timestamp ON AlarmHistory(Timestamp)");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_telemetry_device_timestamp ON TelemetryHistory(DeviceId, Timestamp)");

        SeedTelemetry(connection);
        SeedAlarms(connection);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DefaultConnection"] = _connectionString,
                ["DatabaseSettings:QueryLimit"] = "50"
            })
            .Build();

        _factory = new SqliteConnectionFactory(config);
        _alarmRepo = new SqliteAlarmRepository(_factory, config);
        _telemetryRepo = new SqliteTelemetryRepository(_factory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // Microsoft.Data.Sqlite 默认连接池会保留库文件句柄, 先清池再删除
        SqliteConnection.ClearAllPools();
        DeleteDatabaseFiles();
    }

    // 第一组: 报警历史查询 —— 旧实现整表载入内存后过滤, 新实现在 SQL 内完成过滤与 LIMIT

    /// <summary>报警历史旧实现基线(为对比保留, 不是生产代码): SELECT * 全表读入, LINQ 时间过滤, Take。</summary>
    [Benchmark]
    public async Task<List<AlarmRecord>> Legacy_AlarmHistory()
    {
        await using var connection = await _factory.CreateConnectionAsync();

        var result = (await connection.QueryAsync<AlarmRecord>(
            "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

        DateTime from = _end.AddDays(-1);
        result = [.. result.Where(r => r.Timestamp >= from && r.Timestamp <= _end)];

        return [.. result.Take(AlarmHistoryLimit)];
    }

    /// <summary>报警历史新实现: 过滤条件与 LIMIT 全部下推 SQL。</summary>
    [Benchmark]
    public async Task<List<AlarmRecord>> New_AlarmHistory()
        => await _alarmRepo.GetAlarmHistoryAsync(
            new AlarmHistoryFilter { From = _end.AddDays(-1), To = _end, Limit = AlarmHistoryLimit });

    // 第二组: 报警上下文查询 —— 旧实现把两张表全量读入内存再关联, 新实现按报警窗口逐条下推查询

    /// <summary>
    /// 报警上下文旧实现基线(为对比保留, 不是生产代码):
    /// 全表读入报警, 全表读入遥测(20 万行), GroupJoin 按设备关联, 窗口过滤。
    /// </summary>
    [Benchmark]
    public async Task<List<AlarmWithTelemetry>> Legacy_AlarmContext()
    {
        TimeSpan window = TimeSpan.FromMinutes(5);

        await using var connection = await _factory.CreateConnectionAsync();

        var alarms = (await connection.QueryAsync<AlarmRecord>(
            "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

        var telemetry = (await connection.QueryAsync<SensorReadingRecord>(
            "SELECT * FROM TelemetryHistory ORDER BY Timestamp ASC")).AsList();

        return [.. alarms.Take(AlarmContextLimit)
            .GroupJoin(telemetry, a => a.DeviceId, t => t.DeviceId,
                (alarm, readings) => new { alarm, readings })
            .Select(x => new AlarmWithTelemetry
            {
                Alarm = x.alarm,
                SurroundingTelemetry = [.. x.readings
                    .Where(r => r.Timestamp >= x.alarm.Timestamp - window
                             && r.Timestamp <= x.alarm.Timestamp + window)
                    .OrderBy(r => r.Timestamp)]
            })];
    }

    /// <summary>报警上下文新实现: 报警列表 SQL 过滤, 再按报警窗口逐条下推查遥测。</summary>
    [Benchmark]
    public async Task<List<AlarmWithTelemetry>> New_AlarmContext()
        => await _alarmRepo.GetAlarmsWithTelemetryContextAsync(
            new AlarmHistoryFilter { Limit = AlarmContextLimit }, TimeSpan.FromMinutes(5));

    // 第三组: 趋势查询降采样 —— 旧实现把 30 天原始行全部读回再用 LTTB 压缩, 新实现在 SQL 内按桶取样

    /// <summary>趋势查询旧实现基线(为对比保留, 不是生产代码):
    /// 30 天全量读入内存(20 万行), LTTB 降到 1000 点。</summary>
    [Benchmark]
    public async Task<List<SensorReadingRecord>> Legacy_Trend()
    {
        await using var connection = await _factory.CreateConnectionAsync();

        var rawData = (await connection.QueryAsync<SensorReadingRecord>("""
            SELECT * FROM TelemetryHistory
            WHERE Timestamp >= @From AND Timestamp <= @To
            ORDER BY Timestamp ASC
            """, new { From = _end.AddDays(-30), To = _end })).AsList();

        return LttbDownsampler.Downsample(rawData, TrendTargetPoints);
    }

    /// <summary>趋势查询新实现: SQL 端分桶取样每桶温度 min/max 两个真实行, 超限时兜底 LTTB。</summary>
    [Benchmark]
    public async Task<List<SensorReadingRecord>> New_Trend()
        => await _telemetryRepo.GetTelemetryHistoryAsync(_end.AddDays(-30), _end, TrendTargetPoints);

    /// <summary>写入 20 万行遥测: 最近 30 天均匀分布, 2 台设备交替, 温湿度伪随机游走。</summary>
    private void SeedTelemetry(SqliteConnection connection)
    {
        var rng = new Random(42);
        var start = _end.AddDays(-30);
        double stepSeconds = TimeSpan.FromDays(30).TotalSeconds / TelemetryRows;
        double temperature = 25, humidity = 50;

        using var transaction = connection.BeginTransaction();

        var batch = new List<object>(SeedBatchSize);
        for (int i = 0; i < TelemetryRows; i++)
        {
            temperature = Math.Clamp(temperature + (rng.NextDouble() - 0.5) * 0.5, 15, 35);
            humidity = Math.Clamp(humidity + (rng.NextDouble() - 0.5) * 0.8, 20, 80);

            batch.Add(new
            {
                DeviceId = i % 2 == 0 ? "SIM-A" : "SIM-B",
                Timestamp = start.AddSeconds(i * stepSeconds),
                Temperature = Math.Round(temperature, 2),
                Humidity = Math.Round(humidity, 2),
                StatusCode = (int)DeviceStatus.Online,
                ErrorCode = (int)ErrorCode.NoError,
                QualityCode = (int)DataQuality.Good
            });

            if (batch.Count == SeedBatchSize)
            {
                connection.Execute(TelemetryInsertSql, batch, transaction: transaction);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            connection.Execute(TelemetryInsertSql, batch, transaction: transaction);

        transaction.Commit();
    }

    /// <summary>写入 5000 行报警: 最近 30 天均匀分布, 2 台设备 × 2 种编码交替。</summary>
    private void SeedAlarms(SqliteConnection connection)
    {
        var start = _end.AddDays(-30);
        double stepSeconds = TimeSpan.FromDays(30).TotalSeconds / AlarmRows;

        using var transaction = connection.BeginTransaction();

        var batch = new List<object>(SeedBatchSize);
        for (int i = 0; i < AlarmRows; i++)
        {
            batch.Add(new
            {
                DeviceId = i % 2 == 0 ? "SIM-A" : "SIM-B",
                Timestamp = start.AddSeconds(i * stepSeconds),
                AlarmCode = (i / 2) % 2 == 0 ? "ALM_HIGH" : "ALM_LOW",
                TriggerValue = (double)i,
                QualityCode = (int)DataQuality.Good,
                State = (int)AlarmState.UNACK
            });

            if (batch.Count == SeedBatchSize)
            {
                connection.Execute(AlarmInsertSql, batch, transaction: transaction);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            connection.Execute(AlarmInsertSql, batch, transaction: transaction);

        transaction.Commit();
    }

    private static void DeleteDatabaseFiles()
    {
        // 连接均已 dispose 且池已清空, 直接删除
        foreach (string file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
}
