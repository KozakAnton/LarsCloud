using LarsCloud.Infrastructure;
using LarsCloud.Models;
using Microsoft.Data.Sqlite;

namespace LarsCloud.Services;

public sealed class StateDatabase
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = AppPaths.DatabaseFile,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS files (
                relative_path TEXT PRIMARY KEY COLLATE NOCASE,
                size INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                drive_id TEXT NOT NULL,
                drive_parent_id TEXT NOT NULL,
                last_synced_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_utc TEXT NOT NULL,
                finished_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                uploaded_files INTEGER NOT NULL,
                uploaded_bytes INTEGER NOT NULL,
                error TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_sync_history_finished ON sync_history(finished_utc DESC);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FileState>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FileState>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path,size,last_write_utc_ticks,sha256,drive_id,drive_parent_id,last_synced_utc FROM files";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FileState(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        }
        return result;
    }

    public async Task UpsertFileAsync(FileState file, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO files(relative_path,size,last_write_utc_ticks,sha256,drive_id,drive_parent_id,last_synced_utc)
            VALUES($path,$size,$ticks,$hash,$driveId,$parentId,$synced)
            ON CONFLICT(relative_path) DO UPDATE SET
              relative_path=excluded.relative_path,size=excluded.size,last_write_utc_ticks=excluded.last_write_utc_ticks,
              sha256=excluded.sha256,drive_id=excluded.drive_id,
              drive_parent_id=excluded.drive_parent_id,last_synced_utc=excluded.last_synced_utc;";
        command.Parameters.AddWithValue("$path", file.RelativePath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$ticks", file.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$hash", file.Sha256);
        command.Parameters.AddWithValue("$driveId", file.DriveId);
        command.Parameters.AddWithValue("$parentId", file.DriveParentId);
        command.Parameters.AddWithValue("$synced", file.LastSyncedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE relative_path=$path";
        command.Parameters.AddWithValue("$path", relativePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddHistoryAsync(DateTimeOffset startedUtc, DateTimeOffset finishedUtc, SyncRunStatus status,
        int uploadedFiles, long uploadedBytes, string error, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO sync_history(started_utc,finished_utc,status,uploaded_files,uploaded_bytes,error)
            VALUES($started,$finished,$status,$files,$bytes,$error);
            DELETE FROM sync_history WHERE id NOT IN (SELECT id FROM sync_history ORDER BY id DESC LIMIT 100);";
        command.Parameters.AddWithValue("$started", startedUtc.ToString("O"));
        command.Parameters.AddWithValue("$finished", finishedUtc.ToString("O"));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$files", uploadedFiles);
        command.Parameters.AddWithValue("$bytes", uploadedBytes);
        command.Parameters.AddWithValue("$error", error ?? "");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncHistoryItem>> GetRecentHistoryAsync(int count = 3,
        CancellationToken cancellationToken = default)
    {
        var result = new List<SyncHistoryItem>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id,started_utc,finished_utc,status,uploaded_files,uploaded_bytes,error
            FROM sync_history ORDER BY id DESC LIMIT $count";
        command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = Enum.TryParse<SyncRunStatus>(reader.GetString(3), out var parsed) ? parsed : SyncRunStatus.Failed;
            result.Add(new SyncHistoryItem(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2)), status, reader.GetInt32(4), reader.GetInt64(5), reader.GetString(6)));
        }
        return result;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files; DELETE FROM sync_history; VACUUM;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearFileStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
