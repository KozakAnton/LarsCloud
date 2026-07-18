using System.Security.Cryptography;
using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class FileScanner
{
    private readonly StateDatabase _database;

    public FileScanner(StateDatabase database) => _database = database;

    public Task<ScanResult> ScanAsync(IReadOnlyList<SyncFolderSettings> syncFolders,
        IProgress<(int Files, long Bytes)>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanCoreAsync(syncFolders, progress, cancellationToken), cancellationToken);

    private async Task<ScanResult> ScanCoreAsync(IReadOnlyList<SyncFolderSettings> syncFolders,
        IProgress<(int Files, long Bytes)>? progress,
        CancellationToken cancellationToken)
    {
        if (syncFolders.Count == 0)
            throw new DirectoryNotFoundException("Додайте хоча б одну папку для резервного копіювання.");
        var missing = syncFolders.FirstOrDefault(folder => !Directory.Exists(folder.Path));
        if (missing is not null)
            throw new DirectoryNotFoundException($"Папка «{missing.Name}» не існує або зараз недоступна.");

        var previous = (await _database.GetAllFilesAsync(cancellationToken))
            .ToDictionary(x => SyncFileKey.Create(x.SyncFolderId, x.RelativePath), StringComparer.OrdinalIgnoreCase);
        var changed = new List<LocalFileCandidate>();
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        var totalFiles = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var syncFolder in syncFolders)
        {
            foreach (var fullPath in Directory.EnumerateFiles(syncFolder.Path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info;
                try { info = new FileInfo(fullPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                var relativePath = Path.GetRelativePath(syncFolder.Path, fullPath).Replace('\\', '/');
                var stateKey = SyncFileKey.Create(syncFolder.Id, relativePath);
                current.Add(stateKey);
                totalFiles++;
                totalBytes += info.Length;

                previous.TryGetValue(stateKey, out var state);
                if (state is null || !string.Equals(state.RelativePath, relativePath, StringComparison.Ordinal)
                    || state.Size != info.Length || state.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks)
                {
                    var hash = await ComputeSha256Async(fullPath, cancellationToken);
                    changed.Add(new LocalFileCandidate(syncFolder.Id, syncFolder.Name, fullPath, relativePath,
                        info.Length, info.LastWriteTimeUtc.Ticks, hash, state));
                }
                if (totalFiles % 50 == 0) progress?.Report((totalFiles, totalBytes));
            }
        }

        progress?.Report((totalFiles, totalBytes));
        return new ScanResult(totalBytes, totalFiles, changed.Sum(x => x.Size), changed, current);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024
        });
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
