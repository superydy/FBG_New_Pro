using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO;
using System.Threading.Channels;
using DtsMonitor.App.Models;
using Microsoft.Data.Sqlite;

namespace DtsMonitor.App.Services;

public sealed class SnapshotStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly Channel<StoreJob> _jobs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private bool _disposed;

    public int RetentionDays { get; set; } = 30;

    public SnapshotStore(string dbPath)
    {
        _dbPath = dbPath;
        string? dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _jobs = Channel.CreateUnbounded<StoreJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        InitializeSchema();
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
    }

    public void EnqueueSnapshot(SnapshotModel snapshot)
    {
        if (_disposed)
        {
            return;
        }

        _jobs.Writer.TryWrite(new SnapshotJob(snapshot));
    }

    public void EnqueueAlarm(AlarmRecord alarm)
    {
        if (_disposed)
        {
            return;
        }

        _jobs.Writer.TryWrite(new AlarmJob(alarm));
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

    private void InitializeSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS snapshot_index (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ms INTEGER NOT NULL,
    channel INTEGER NOT NULL,
    point_count INTEGER NOT NULL,
    min_temp REAL NOT NULL,
    max_temp REAL NOT NULL,
    avg_temp REAL NOT NULL,
    max_pos_m REAL NOT NULL,
    alarm_count INTEGER NOT NULL,
    status_ok INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_snapshot_ts ON snapshot_index(ts_ms);

CREATE TABLE IF NOT EXISTS profile_chunks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL,
    chunk_no INTEGER NOT NULL,
    positions_blob BLOB NOT NULL,
    temps_blob BLOB NOT NULL,
    FOREIGN KEY(snapshot_id) REFERENCES snapshot_index(id)
);

CREATE INDEX IF NOT EXISTS idx_profile_snapshot ON profile_chunks(snapshot_id, chunk_no);

CREATE TABLE IF NOT EXISTS alarm_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ms INTEGER NOT NULL,
    channel INTEGER NOT NULL,
    position_m REAL NOT NULL,
    temperature_c REAL NOT NULL,
    sensor_index INTEGER NOT NULL,
    zone_no INTEGER NOT NULL DEFAULT 0,
    type_code TEXT,
    type_text TEXT,
    source_key TEXT,
    note TEXT
);

CREATE INDEX IF NOT EXISTS idx_alarm_ts ON alarm_events(ts_ms);
";
        cmd.ExecuteNonQuery();
        MigrateAlarmEventsSchema(conn);
    }

    private static void MigrateAlarmEventsSchema(SqliteConnection conn)
    {
        bool hasLegacyLevelColumn;
        bool hasZoneColumn;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(alarm_events);";
            using var reader = cmd.ExecuteReader();
            hasLegacyLevelColumn = false;
            hasZoneColumn = false;
            while (reader.Read())
            {
                string columnName = reader.GetString(1);
                if (string.Equals(columnName, "level", StringComparison.OrdinalIgnoreCase))
                {
                    hasLegacyLevelColumn = true;
                }

                if (string.Equals(columnName, "zone_no", StringComparison.OrdinalIgnoreCase))
                {
                    hasZoneColumn = true;
                }
            }
        }

        if (!hasLegacyLevelColumn && hasZoneColumn)
        {
            return;
        }

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
ALTER TABLE alarm_events RENAME TO alarm_events_legacy;

CREATE TABLE alarm_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ms INTEGER NOT NULL,
    channel INTEGER NOT NULL,
    position_m REAL NOT NULL,
    temperature_c REAL NOT NULL,
    sensor_index INTEGER NOT NULL,
    zone_no INTEGER NOT NULL DEFAULT 0,
    type_code TEXT,
    type_text TEXT,
    source_key TEXT,
    note TEXT
);

INSERT INTO alarm_events (id, ts_ms, channel, position_m, temperature_c, sensor_index, zone_no, type_code, type_text, source_key, note)
SELECT id,
       ts_ms,
       channel,
       position_m,
       temperature_c,
       sensor_index,
       0,
       '',
       '',
       '',
       note
FROM alarm_events_legacy;

DROP TABLE alarm_events_legacy;

CREATE INDEX IF NOT EXISTS idx_alarm_ts ON alarm_events(ts_ms);";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private async Task WriterLoopAsync(CancellationToken token)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(token);

        int writeCounter = 0;

        while (await _jobs.Reader.WaitToReadAsync(token))
        {
            while (_jobs.Reader.TryRead(out StoreJob? job))
            {
                switch (job)
                {
                    case SnapshotJob snapshotJob:
                        await WriteSnapshotAsync(conn, snapshotJob.Snapshot, token);
                        break;
                    case AlarmJob alarmJob:
                        await WriteAlarmAsync(conn, alarmJob.Alarm, token);
                        break;
                }

                writeCounter++;
                if (writeCounter % 50 == 0)
                {
                    await CleanupOldDataAsync(conn, token);
                }
            }
        }
    }

    private static async Task WriteSnapshotAsync(SqliteConnection conn, SnapshotModel snapshot, CancellationToken token)
    {
        using var tx = conn.BeginTransaction();

        long snapshotId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO snapshot_index (ts_ms, channel, point_count, min_temp, max_temp, avg_temp, max_pos_m, alarm_count, status_ok)
VALUES ($ts, $channel, $points, $min, $max, $avg, $maxpos, $alarmCount, $statusOk);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$ts", snapshot.TimestampMs);
            cmd.Parameters.AddWithValue("$channel", snapshot.Channel);
            cmd.Parameters.AddWithValue("$points", snapshot.PositionsM.Length);
            cmd.Parameters.AddWithValue("$min", snapshot.MinTemp);
            cmd.Parameters.AddWithValue("$max", snapshot.MaxTemp);
            cmd.Parameters.AddWithValue("$avg", snapshot.AvgTemp);
            cmd.Parameters.AddWithValue("$maxpos", snapshot.MaxPosM);
            cmd.Parameters.AddWithValue("$alarmCount", snapshot.Alarms.Length);
            cmd.Parameters.AddWithValue("$statusOk", snapshot.StatusOk ? 1 : 0);

            object? idObj = await cmd.ExecuteScalarAsync(token);
            snapshotId = Convert.ToInt64(idObj);
        }

        const int chunkSize = 1000;
        int chunkNo = 0;
        for (int offset = 0; offset < snapshot.PositionsM.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, snapshot.PositionsM.Length - offset);
            byte[] posBlob = CompressFloatSlice(snapshot.PositionsM, offset, count);
            byte[] tempBlob = CompressFloatSlice(snapshot.TemperaturesC, offset, count);

            await using var chunkCmd = conn.CreateCommand();
            chunkCmd.Transaction = tx;
            chunkCmd.CommandText = @"
INSERT INTO profile_chunks (snapshot_id, chunk_no, positions_blob, temps_blob)
VALUES ($snapshotId, $chunkNo, $posBlob, $tempBlob);";
            chunkCmd.Parameters.AddWithValue("$snapshotId", snapshotId);
            chunkCmd.Parameters.AddWithValue("$chunkNo", chunkNo++);
            chunkCmd.Parameters.Add("$posBlob", SqliteType.Blob).Value = posBlob;
            chunkCmd.Parameters.Add("$tempBlob", SqliteType.Blob).Value = tempBlob;
            await chunkCmd.ExecuteNonQueryAsync(token);
        }

        tx.Commit();
    }

    private static async Task WriteAlarmAsync(SqliteConnection conn, AlarmRecord alarm, CancellationToken token)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO alarm_events (ts_ms, channel, position_m, temperature_c, sensor_index, zone_no, type_code, type_text, source_key, note)
VALUES ($ts, $channel, $position, $temp, $sensorIndex, $zoneNo, $typeCode, $typeText, $sourceKey, $note);";
        cmd.Parameters.AddWithValue("$ts", alarm.TimestampMs);
        cmd.Parameters.AddWithValue("$channel", alarm.Channel);
        cmd.Parameters.AddWithValue("$position", alarm.PositionM);
        cmd.Parameters.AddWithValue("$temp", alarm.TemperatureC);
        cmd.Parameters.AddWithValue("$sensorIndex", alarm.SensorIndex);
        cmd.Parameters.AddWithValue("$zoneNo", alarm.ZoneNo);
        cmd.Parameters.AddWithValue("$typeCode", string.IsNullOrWhiteSpace(alarm.TypeCode) ? string.Empty : alarm.TypeCode);
        cmd.Parameters.AddWithValue("$typeText", string.IsNullOrWhiteSpace(alarm.TypeText) ? string.Empty : alarm.TypeText);
        cmd.Parameters.AddWithValue("$sourceKey", string.IsNullOrWhiteSpace(alarm.SourceKey) ? string.Empty : alarm.SourceKey);
        cmd.Parameters.AddWithValue("$note", string.IsNullOrWhiteSpace(alarm.DetailText) ? string.Empty : alarm.DetailText);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private async Task CleanupOldDataAsync(SqliteConnection conn, CancellationToken token)
    {
        if (RetentionDays <= 0)
        {
            return;
        }

        long thresholdMs = DateTimeOffset.Now.AddDays(-RetentionDays).ToUnixTimeMilliseconds();

        using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
DELETE FROM profile_chunks
WHERE snapshot_id IN (SELECT id FROM snapshot_index WHERE ts_ms < $threshold);";
            cmd.Parameters.AddWithValue("$threshold", thresholdMs);
            await cmd.ExecuteNonQueryAsync(token);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM snapshot_index WHERE ts_ms < $threshold;";
            cmd.Parameters.AddWithValue("$threshold", thresholdMs);
            await cmd.ExecuteNonQueryAsync(token);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM alarm_events WHERE ts_ms < $threshold;";
            cmd.Parameters.AddWithValue("$threshold", thresholdMs);
            await cmd.ExecuteNonQueryAsync(token);
        }

        tx.Commit();
    }

    private static byte[] CompressFloatSlice(float[] values, int offset, int count)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(count);
            for (int i = 0; i < count; i++)
            {
                writer.Write(values[offset + i]);
            }
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _jobs.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _cts.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private abstract record StoreJob;
    private sealed record SnapshotJob(SnapshotModel Snapshot) : StoreJob;
    private sealed record AlarmJob(AlarmRecord Alarm) : StoreJob;
}
