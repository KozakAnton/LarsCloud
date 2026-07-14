namespace LarsCloud.Models;

public sealed record FileState(
    string RelativePath,
    long Size,
    long LastWriteUtcTicks,
    string Sha256,
    string DriveId,
    string DriveParentId,
    DateTimeOffset LastSyncedUtc);

public sealed record LocalFileCandidate(
    string FullPath,
    string RelativePath,
    long Size,
    long LastWriteUtcTicks,
    string Sha256,
    FileState? PreviousState);

public sealed record ScanResult(
    long TotalBytes,
    int TotalFiles,
    long UploadBytes,
    IReadOnlyList<LocalFileCandidate> ChangedFiles,
    IReadOnlySet<string> CurrentRelativePaths);

public enum SyncRunStatus { Running, Success, Failed, Cancelled }

public sealed record SyncProgress(
    SyncRunStatus Status,
    string Message,
    string CurrentFile,
    int ProcessedFiles,
    int TotalFiles,
    long UploadedBytes,
    long TotalUploadBytes,
    double BytesPerSecond,
    TimeSpan? Remaining)
{
    public double Percent => TotalUploadBytes > 0
        ? Math.Clamp(UploadedBytes * 100d / TotalUploadBytes, 0, 100)
        : TotalFiles > 0 ? Math.Clamp(ProcessedFiles * 100d / TotalFiles, 0, 100) : 0;
}

public sealed record SyncHistoryItem(
    long Id,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    SyncRunStatus Status,
    int UploadedFiles,
    long UploadedBytes,
    string Error);

public sealed record SyncResult(SyncRunStatus Status, int UploadedFiles, long UploadedBytes, string Error);
