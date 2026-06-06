using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartEdgeHMI.Models;

namespace SmartEdgeHMI.Services;

public class SqliteRepository(IConfiguration config) : ISqliteRepository
{
    private readonly string _connectionString = config["DatabaseSettings:DefaultConnection"] ?? "Data Source=SmartEdgeHMI.db";
    private readonly string _queryLimit = config["DatabaseSettings:QueryLimit"] ?? "10";

    public List<AlarmHistory> GetAlarmHistory()
    {
        var list = new List<AlarmHistory>();

        // using 确保连接用完即关闭
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = $"SELECT DeviceId, Timestamp, AlarmCode FROM AlarmHistory ORDER BY Timestamp DESC LIMIT {_queryLimit}";

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new AlarmHistory
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
}
