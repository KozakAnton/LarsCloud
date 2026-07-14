namespace LarsCloud.Models;

public sealed class AppSettings
{
    public string LocalFolder { get; set; } = "";
    public int SyncIntervalDays { get; set; } = 2;
    public bool AutoStart { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool ErrorNotificationsEnabled { get; set; } = true;
    public bool DeleteRemoteWhenLocalDeleted { get; set; }
    public bool SyncPaused { get; set; }
    public DateTimeOffset? LastSyncUtc { get; set; }
    public DateTimeOffset? NextSyncUtc { get; set; }
    public string? GoogleRootFolderId { get; set; }
    public string? GoogleDeviceFolderId { get; set; }
    public string? GoogleDriveWebUrl { get; set; }
    public bool StartMinimized { get; set; } = true;

    public void Normalize()
    {
        SyncIntervalDays = Math.Clamp(SyncIntervalDays, 1, 7);
        LocalFolder = LocalFolder?.Trim() ?? "";
    }
}
