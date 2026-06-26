using System.IO;
using DtsMonitor.App.Models;
using Microsoft.Data.Sqlite;

namespace DtsMonitor.App.Services;

public sealed class SnapshotHistoryStore
{
    private readonly string _connectionString;

    public SnapshotHistoryStore(string dbPath)
    {
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<IReadOnlyList<AlarmRecord>> QueryAlarmEventsAsync(DateTime startLocal, DateTime endLocal, int limit = 500)
    {
        long startMs = new DateTimeOffset(startLocal).ToUnixTimeMilliseconds();
        long endMs = new DateTimeOffset(endLocal).ToUnixTimeMilliseconds();

        var result = new List<AlarmRecord>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT ts_ms, channel, position_m, temperature_c, sensor_index, zone_no, type_code, type_text, source_key, note
FROM alarm_events
WHERE ts_ms BETWEEN $start AND $end
ORDER BY ts_ms DESC
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$start", startMs);
        cmd.Parameters.AddWithValue("$end", endMs);
        cmd.Parameters.AddWithValue("$limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AlarmRecord
            {
                TimestampMs = reader.GetInt64(0),
                Channel = reader.GetInt32(1),
                PositionM = reader.GetFloat(2),
                TemperatureC = reader.GetFloat(3),
                SensorIndex = reader.GetInt32(4),
                ZoneNo = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                TypeCode = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                TypeText = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                SourceKey = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                DetailText = reader.IsDBNull(9) ? string.Empty : reader.GetString(9)
            });
        }

        return result;
    }

    public async Task ClearAlarmEventsAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM alarm_events;";
        await cmd.ExecuteNonQueryAsync();

        await using var vacuum = conn.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        await vacuum.ExecuteNonQueryAsync();
    }
}
