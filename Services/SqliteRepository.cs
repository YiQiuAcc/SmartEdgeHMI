using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Services;

public class SqliteRepository(IConfiguration config) : ISqliteRepository
{
    private readonly string _connectionString = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
    private readonly int _queryLimit = int.TryParse(config["DatabaseSettings:QueryLimit"], out int limit) ? limit : 10;

    private bool _initialized;

    private void EnsureTable()
    {
        if (_initialized) return;
        _initialized = true;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AlarmHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                AlarmCode TEXT NOT NULL,
                TriggerValue REAL NOT NULL DEFAULT 0
            )
            """;
        command.ExecuteNonQuery();
    }

    public List<AlarmRecordEntity> GetAlarmHistory()
    {
        EnsureTable();

        var list = new List<AlarmRecordEntity>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT DeviceId, Timestamp, AlarmCode FROM AlarmHistory ORDER BY Timestamp DESC LIMIT @Limit";
        command.Parameters.AddWithValue("@Limit", _queryLimit);

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new AlarmRecordEntity
                {
                    DeviceId = reader.GetString(0),
                    Timestamp = reader.GetDateTime(1),
                    AlarmCode = reader.GetString(2)
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取报警历史失败");
        }

        return list;
    }

    public void SaveAlarmRecord(AlarmRecordEntity alarmRecord)
    {
        EnsureTable();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO AlarmHistory (DeviceId, Timestamp, AlarmCode) VALUES (@DeviceId, @Timestamp, @AlarmCode)";
        command.Parameters.AddWithValue("@DeviceId", alarmRecord.DeviceId);
        command.Parameters.AddWithValue("@Timestamp", alarmRecord.Timestamp);
        command.Parameters.AddWithValue("@AlarmCode", alarmRecord.AlarmCode);
        try
        {
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存报警记录失败");
        }
    }
}
