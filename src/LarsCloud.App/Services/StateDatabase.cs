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

    public async Task InitializeAsync(string? legacySyncFolderId = null, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var filesTableExists = await TableExistsAsync(connection, "files", cancellationToken);
        if (!filesTableExists)
        {
            await CreateFilesTableAsync(connection, "files", cancellationToken);
        }
        else
        {
            var columns = await GetTableColumnsAsync(connection, "files", cancellationToken);
            if (!columns.Contains("folder_id"))
                await MigrateSingleFolderFilesAsync(connection, legacySyncFolderId, cancellationToken);
        }

        var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
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

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task CreateFilesTableAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $@"
            CREATE TABLE {table} (
                folder_id TEXT NOT NULL COLLATE NOCASE,
                relative_path TEXT NOT NULL COLLATE NOCASE,
                size INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                drive_id TEXT NOT NULL,
                drive_parent_id TEXT NOT NULL,
                last_synced_utc TEXT NOT NULL,
                PRIMARY KEY(folder_id, relative_path)
            );";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateSingleFolderFilesAsync(SqliteConnection connection, string? legacySyncFolderId,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        var dropTemporary = connection.CreateCommand();
        dropTemporary.Transaction = transaction;
        dropTemporary.CommandText = "DROP TABLE IF EXISTS files_v2";
        await dropTemporary.ExecuteNonQueryAsync(cancellationToken);
        await CreateFilesTableAsync(connection, "files_v2", cancellationToken, transaction);

        var copy = connection.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = @"
            INSERT OR IGNORE INTO files_v2(folder_id,relative_path,size,last_write_utc_ticks,sha256,drive_id,drive_parent_id,last_synced_utc)
            SELECT $folderId,relative_path,size,last_write_utc_ticks,sha256,drive_id,drive_parent_id,last_synced_utc FROM files;
            DROP TABLE files;
            ALTER TABLE files_v2 RENAME TO files;";
        copy.Parameters.AddWithValue("$folderId",
            string.IsNullOrWhiteSpace(legacySyncFolderId) ? "legacy-single-folder" : legacySyncFolderId);
        await copy.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<FileState>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FileState>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT folder_id,relative_path,size,last_write_utc_ticks,sha256,drive_id,drive_parent_id,last_synced_utc FROM files";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FileState(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7))));
        }
        return result;
    }

    public async Task UpsertFileAsync(FileState file, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO files(folder_id,relative_path,size,last_write_utc_ticks,sha256,drive_id,drive_parent_id,last_synced_utc)
            VALUES($folderId,$path,$size,$ticks,$hash,$driveId,$parentId,$synced)
            ON CONFLICT(folder_id,relative_path) DO UPDATE SET
              folder_id=excluded.folder_id,relative_path=excluded.relative_path,size=excluded.size,last_write_utc_ticks=excluded.last_write_utc_ticks,
              sha256=excluded.sha256,drive_id=excluded.drive_id,
              drive_parent_id=excluded.drive_parent_id,last_synced_utc=excluded.last_synced_utc;";
        command.Parameters.AddWithValue("$folderId", file.SyncFolderId);
        command.Parameters.AddWithValue("$path", file.RelativePath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$ticks", file.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$hash", file.Sha256);
        command.Parameters.AddWithValue("$driveId", file.DriveId);
        command.Parameters.AddWithValue("$parentId", file.DriveParentId);
        command.Parameters.AddWithValue("$synced", file.LastSyncedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveFileAsync(string syncFolderId, string relativePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE folder_id=$folderId AND relative_path=$path";
        command.Parameters.AddWithValue("$folderId", syncFolderId);
        command.Parameters.AddWithValue("$path", relativePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveFolderFilesAsync(string syncFolderId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE folder_id=$folderId";
        command.Parameters.AddWithValue("$folderId", syncFolderId);
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
