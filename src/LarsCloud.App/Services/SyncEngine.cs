using System.Diagnostics;
using System.Net;
using LarsCloud.Infrastructure;
using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class SyncEngine
{
    private readonly SettingsService _settings;
    private readonly ConnectivityService _connectivity;
    private readonly GoogleOAuthService _oauth;
    private readonly GoogleDriveService _drive;
    private readonly FileScanner _scanner;
    private readonly StateDatabase _database;
    private readonly LogService _log;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _currentRun;

    public SyncEngine(SettingsService settings, ConnectivityService connectivity, GoogleOAuthService oauth,
        GoogleDriveService drive, FileScanner scanner, StateDatabase database, LogService log)
    {
        _settings = settings;
        _connectivity = connectivity;
        _oauth = oauth;
        _drive = drive;
        _scanner = scanner;
        _database = database;
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public event EventHandler<SyncProgress>? ProgressChanged;
    public event EventHandler<SyncResult>? Completed;
    public event EventHandler<ScanResult>? AnalysisCompleted;

    public async Task<ScanResult> AnalyzeAsync(IProgress<(int Files, long Bytes)>? progress = null,
        CancellationToken cancellationToken = default) =>
        await _scanner.ScanAsync(_settings.Current.SyncFolders, progress, cancellationToken);

    public async Task<SyncResult> RunAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            return new SyncResult(SyncRunStatus.Running, 0, 0, "Синхронізація вже виконується.");

        var started = DateTimeOffset.UtcNow;
        var uploadedFiles = 0;
        long uploadedBytes = 0;
        IsRunning = true;
        _currentRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _currentRun.Token;
        Report(new SyncProgress(SyncRunStatus.Running, "Підготовка до синхронізації…", "", 0, 0, 0, 0, 0, null));

        try
        {
            var settings = _settings.Current;
            if (settings.SyncPaused && !manual) throw new SyncPausedException("Автоматичну синхронізацію призупинено.");
            if (settings.SyncFolders.Count == 0)
                throw new DirectoryNotFoundException("Додайте хоча б одну папку для резервного копіювання.");
            var missingFolder = settings.SyncFolders.FirstOrDefault(folder => !Directory.Exists(folder.Path));
            if (missingFolder is not null)
                throw new DirectoryNotFoundException($"Папка «{missingFolder.Name}» не існує або зараз недоступна.");
            if (!await _connectivity.IsOnlineAsync(token))
                throw new NetworkUnavailableException("Немає підключення до інтернету.");
            if (!_oauth.IsAuthenticated)
                throw new ReauthenticationRequiredException("Google Drive не підключений.");

            var about = await _drive.GetAboutAsync(token);
            var folders = await _drive.EnsureBackupFoldersAsync(token);
            settings.GoogleRootFolderId = folders.Root.Id;
            settings.GoogleDeviceFolderId = folders.Device.Id;
            settings.GoogleDriveWebUrl = folders.Device.WebUrl;
            await _settings.SaveAsync(token);

            Report(new SyncProgress(SyncRunStatus.Running, "Підготовка папок на Google Drive…", "", 0, 0, 0, 0, 0, null));
            await MigrateStoredFilesToFolderHierarchyAsync(settings.SyncFolders, folders.Device.Id, token);

            Report(new SyncProgress(SyncRunStatus.Running, "Аналіз локальних папок…", "", 0, 0, 0, 0, 0, null));
            var scan = await _scanner.ScanAsync(settings.SyncFolders, null, token);
            AnalysisCompleted?.Invoke(this, scan);
            if (about.Quota.Remaining is long remaining && scan.UploadBytes > remaining)
                throw new DriveApiException($"Недостатньо місця на Google Drive. Потрібно {Formatters.Bytes(scan.UploadBytes)}, доступно {Formatters.Bytes(remaining)}.");

            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < scan.ChangedFiles.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var fileIndex = index;
                var file = scan.ChangedFiles[index];
                var relativeDirectory = Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/') ?? "";
                var driveDirectory = BuildDriveDirectory(file.SyncFolderName, relativeDirectory);
                var parentId = await _drive.EnsureRelativeFolderAsync(driveDirectory, folders.Device.Id, token);
                var alreadyUploaded = uploadedBytes;
                var perFileProgress = new Progress<FileUploadProgress>(chunk =>
                {
                    var totalNow = alreadyUploaded + chunk.UploadedBytes;
                    var speed = stopwatch.Elapsed.TotalSeconds > 0 ? totalNow / stopwatch.Elapsed.TotalSeconds : 0;
                    var remainingBytes = Math.Max(0, scan.UploadBytes - totalNow);
                    var eta = speed > 1 ? TimeSpan.FromSeconds(remainingBytes / speed) : (TimeSpan?)null;
                    Report(new SyncProgress(SyncRunStatus.Running, "Завантаження файлів…", DisplayPath(file),
                        fileIndex, scan.ChangedFiles.Count, totalNow, scan.UploadBytes, speed, eta));
                });

                var remote = await _drive.UploadFileAsync(file, parentId, file.PreviousState?.DriveId, perFileProgress, token);
                uploadedFiles++;
                uploadedBytes += file.Size;
                await _database.UpsertFileAsync(new FileState(file.SyncFolderId, file.RelativePath, file.Size, file.LastWriteUtcTicks,
                    file.Sha256, remote.Id, parentId, DateTimeOffset.UtcNow), token);
                Report(new SyncProgress(SyncRunStatus.Running, "Завантаження файлів…", DisplayPath(file),
                    index + 1, scan.ChangedFiles.Count, uploadedBytes, scan.UploadBytes,
                    stopwatch.Elapsed.TotalSeconds > 0 ? uploadedBytes / stopwatch.Elapsed.TotalSeconds : 0, null));
            }

            if (settings.DeleteRemoteWhenLocalDeleted)
            {
                var stored = await _database.GetAllFilesAsync(token);
                var activeFolderIds = settings.SyncFolders.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var old in stored.Where(x => activeFolderIds.Contains(x.SyncFolderId)
                                                      && !scan.CurrentFileKeys.Contains(SyncFileKey.Create(x.SyncFolderId, x.RelativePath))))
                {
                    token.ThrowIfCancellationRequested();
                    await _drive.DeleteFileAsync(old.DriveId, token);
                    await _database.RemoveFileAsync(old.SyncFolderId, old.RelativePath, token);
                }
            }

            settings.LastSyncUtc = DateTimeOffset.UtcNow;
            settings.NextSyncUtc = settings.LastSyncUtc.Value.AddDays(settings.SyncIntervalDays);
            await _settings.SaveAsync(token);
            var result = new SyncResult(SyncRunStatus.Success, uploadedFiles, uploadedBytes, "");
            await _database.AddHistoryAsync(started, DateTimeOffset.UtcNow, result.Status, uploadedFiles, uploadedBytes, "", token);
            await _log.InfoAsync($"Sync completed. Files={uploadedFiles}; bytes={uploadedBytes}.");
            Report(new SyncProgress(SyncRunStatus.Success,
                uploadedFiles == 0 ? "Усі файли вже актуальні" : "Синхронізацію успішно завершено",
                "", scan.ChangedFiles.Count, scan.ChangedFiles.Count, uploadedBytes, scan.UploadBytes, 0, TimeSpan.Zero));
            Completed?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = new SyncResult(SyncRunStatus.Cancelled, uploadedFiles, uploadedBytes, "Синхронізацію скасовано.");
            await RecordFailureSafelyAsync(started, result);
            Report(new SyncProgress(SyncRunStatus.Cancelled, result.Error, "", uploadedFiles, uploadedFiles, uploadedBytes, uploadedBytes, 0, null));
            Completed?.Invoke(this, result);
            return result;
        }
        catch (SyncPausedException ex)
        {
            var result = new SyncResult(SyncRunStatus.Cancelled, uploadedFiles, uploadedBytes, ex.Message);
            Report(new SyncProgress(SyncRunStatus.Cancelled, ex.Message, "", 0, 0, 0, 0, 0, null));
            return result;
        }
        catch (Exception ex)
        {
            var result = new SyncResult(SyncRunStatus.Failed, uploadedFiles, uploadedBytes, FriendlyMessage(ex));
            await RecordFailureSafelyAsync(started, result);
            await _log.ErrorAsync("Sync failed", ex);
            Report(new SyncProgress(SyncRunStatus.Failed, result.Error, "", uploadedFiles, uploadedFiles, uploadedBytes, uploadedBytes, 0, null));
            Completed?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsRunning = false;
            _currentRun?.Dispose();
            _currentRun = null;
            _runGate.Release();
        }
    }

    public void CancelCurrent() => _currentRun?.Cancel();

    private async Task MigrateStoredFilesToFolderHierarchyAsync(IReadOnlyList<SyncFolderSettings> syncFolders,
        string deviceFolderId, CancellationToken cancellationToken)
    {
        var byId = syncFolders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var stored = await _database.GetAllFilesAsync(cancellationToken);
        foreach (var state in stored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(state.SyncFolderId, out var syncFolder)) continue;
            var relativeDirectory = Path.GetDirectoryName(state.RelativePath)?.Replace('\\', '/') ?? "";
            var destinationParentId = await _drive.EnsureRelativeFolderAsync(
                BuildDriveDirectory(syncFolder.Name, relativeDirectory), deviceFolderId, cancellationToken);
            if (string.Equals(state.DriveParentId, destinationParentId, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                await _drive.MoveFileAsync(state.DriveId, destinationParentId, cancellationToken);
                await _database.UpsertFileAsync(state with
                {
                    DriveParentId = destinationParentId,
                    LastSyncedUtc = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
            catch (DriveApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // A manually removed remote file is discovered as changed by the scan that follows.
                await _database.RemoveFileAsync(state.SyncFolderId, state.RelativePath, cancellationToken);
            }
        }
    }

    private static string BuildDriveDirectory(string syncFolderName, string relativeDirectory) =>
        string.IsNullOrWhiteSpace(relativeDirectory)
            ? syncFolderName
            : $"{syncFolderName}/{relativeDirectory.Trim('/')}";

    private static string DisplayPath(LocalFileCandidate file) => $"{file.SyncFolderName}/{file.RelativePath}";

    private void Report(SyncProgress progress) => ProgressChanged?.Invoke(this, progress);

    private async Task RecordFailureSafelyAsync(DateTimeOffset started, SyncResult result)
    {
        try { await _database.AddHistoryAsync(started, DateTimeOffset.UtcNow, result.Status, result.UploadedFiles, result.UploadedBytes, result.Error); }
        catch (Exception ex) { await _log.ErrorAsync("Could not save sync history", ex); }
    }

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        DirectoryNotFoundException => exception.Message,
        UnauthorizedAccessException => "Немає доступу до вибраної папки або одного з файлів.",
        NetworkUnavailableException => exception.Message,
        ReauthenticationRequiredException => exception.Message,
        DriveApiException => exception.Message,
        IOException => "Не вдалося прочитати один із файлів. Можливо, він відкритий іншою програмою або був змінений під час копіювання.",
        _ => "Помилка синхронізації файлів. Перегляньте журнал для деталей."
    };
}

public sealed class NetworkUnavailableException : Exception { public NetworkUnavailableException(string message) : base(message) { } }
public sealed class SyncPausedException : Exception { public SyncPausedException(string message) : base(message) { } }
