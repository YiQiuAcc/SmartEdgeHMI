using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Data.Filters;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Data.Repositories;

/// <summary>报警记录持久化仓储: 查询、写入、状态更新、上下文关联遥测分析。</summary>
public sealed class SqliteAlarmRepository(SqliteConnectionFactory factory, IConfiguration config) : IAlarmRepository
{
    private readonly int _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

    public async Task<List<AlarmRecord>> GetAlarmHistoryAsync(AlarmHistoryFilter? filter = null)
    {
        try
        {
            await using var connection = await factory.CreateConnectionAsync();

            return await QueryAlarmsAsync(connection, filter, _queryLimit);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警历史失败");
            return [];
        }
    }

    public async Task<long> SaveAlarmRecordAsync(AlarmRecord alarmRecord)
    {
        try
        {
            await using var connection = await factory.CreateConnectionAsync();

            return await connection.ExecuteScalarAsync<long>("""
                INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode, TriggerValue, QualityCode, State)
                VALUES (@DeviceId, @Timestamp, @AlarmCode, @TriggerValue, @QualityCode, @State);
                SELECT last_insert_rowid();
                """, alarmRecord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存报警记录失败");
            return 0;
        }
    }

    public async Task UpdateAlarmStatesAsync(IEnumerable<AlarmRecord> alarms)
    {
        try
        {
            await using var connection = await factory.CreateConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await connection.ExecuteAsync("""
                UPDATE AlarmHistory
                SET State = @State, AcknowledgedAt = @AcknowledgedAt, ClearedAt = @ClearedAt
                WHERE Id = @Id
                """, alarms, transaction: transaction);

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量更新报警状态失败");
        }
    }

    public async Task<List<AlarmWithTelemetry>> GetAlarmsWithTelemetryContextAsync(
        AlarmHistoryFilter? filter = null, TimeSpan? telemetryWindow = null)
    {
        TimeSpan window = telemetryWindow ?? TimeSpan.FromMinutes(5);

        try
        {
            await using var connection = await factory.CreateConnectionAsync();

            // 两段式查询: 先按过滤条件取报警列表(与 GetAlarmHistoryAsync 共用 SQL), 再逐条按时间窗口查遥测,
            // 避免将报警表和遥测表整表载入内存
            List<AlarmRecord> alarms = await QueryAlarmsAsync(connection, filter, _queryLimit);

            var results = new List<AlarmWithTelemetry>(alarms.Count);

            foreach (AlarmRecord alarm in alarms)
            {
                var telemetry = (await connection.QueryAsync<SensorReadingRecord>("""
                    SELECT * FROM TelemetryHistory
                    WHERE DeviceId = @DeviceId
                        AND Timestamp >= @Start AND Timestamp <= @End
                    ORDER BY Timestamp ASC
                    """, new
                {
                    alarm.DeviceId,
                    Start = alarm.Timestamp - window,
                    End = alarm.Timestamp + window
                })).AsList();

                results.Add(new AlarmWithTelemetry
                {
                    Alarm = alarm,
                    SurroundingTelemetry = telemetry
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警上下文失败");
            return [];
        }
    }

    /// <summary>报警列表共用查询: 过滤条件与 Limit 下推到 SQL, 供历史查询与上下文查询复用, 避免两处 SQL 漂移。</summary>
    private static async Task<List<AlarmRecord>> QueryAlarmsAsync(
        SqliteConnection connection, AlarmHistoryFilter? filter, int queryLimit)
    {
        // 参数化占位符保证 null 条件短路为"不过滤"
        return [.. await connection.QueryAsync<AlarmRecord>("""
            SELECT * FROM AlarmHistory
            WHERE (@From IS NULL OR Timestamp >= @From)
                AND (@To IS NULL OR Timestamp <= @To)
                AND (@DeviceId IS NULL OR DeviceId = @DeviceId COLLATE NOCASE)
                AND (@AlarmCode IS NULL OR AlarmCode = @AlarmCode COLLATE NOCASE)
            ORDER BY Timestamp DESC
            LIMIT @Limit
            """, new
        {
            From = filter?.From,
            To = filter?.To,
            DeviceId = filter?.DeviceId,
            AlarmCode = filter?.AlarmCode,
            Limit = filter?.Limit ?? queryLimit
        })];
    }
}
