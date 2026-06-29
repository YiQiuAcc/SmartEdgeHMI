using Dapper;
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

            var result = (await connection.QueryAsync<AlarmRecord>(
                "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

            if (filter is not null)
            {
                result = [.. result
                    .Where(r => (!filter.From.HasValue || r.Timestamp >= filter.From.Value) &&
                    (!filter.To.HasValue || r.Timestamp <= filter.To.Value) &&
                    (filter.DeviceId is null || r.DeviceId.Equals(filter.DeviceId, StringComparison.OrdinalIgnoreCase)) &&
                    (filter.AlarmCode is null || r.AlarmCode.Equals(filter.AlarmCode, StringComparison.OrdinalIgnoreCase)))];
            }

            return [.. result.Take(filter?.Limit ?? _queryLimit)];
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

            var alarms = (await connection.QueryAsync<AlarmRecord>(
                "SELECT * FROM AlarmHistory ORDER BY Timestamp DESC")).AsList();

            var telemetry = (await connection.QueryAsync<SensorReadingRecord>(
                "SELECT * FROM TelemetryHistory ORDER BY Timestamp ASC")).AsList();

            if (filter is not null)
            {
                alarms = [.. alarms
                    .Where(r => (!filter.From.HasValue || r.Timestamp >= filter.From.Value) &&
                    (!filter.To.HasValue || r.Timestamp <= filter.To.Value) &&
                    (filter.DeviceId is null || r.DeviceId.Equals(filter.DeviceId, StringComparison.OrdinalIgnoreCase)) &&
                    (filter.AlarmCode is null || r.AlarmCode.Equals(filter.AlarmCode, StringComparison.OrdinalIgnoreCase)))];
            }

            int limit = filter?.Limit ?? _queryLimit;
            var limitedAlarms = alarms.Take(limit).ToList();

            return [.. limitedAlarms
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
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警上下文失败");
            return [];
        }
    }
}
