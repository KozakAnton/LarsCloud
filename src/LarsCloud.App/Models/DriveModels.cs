namespace LarsCloud.Models;

public sealed record DriveQuota(long? Limit, long Usage)
{
    public long? Remaining => Limit is null ? null : Math.Max(0, Limit.Value - Usage);
    public double UsedPercent => Limit is > 0 ? Math.Clamp(Usage * 100d / Limit.Value, 0, 100) : 0;
}

public sealed record DriveAbout(string DisplayName, string Email, DriveQuota Quota);
public sealed record DriveFolder(string Id, string Name, string WebUrl);
public sealed record DriveFile(string Id, string Name, string? Md5Checksum, string? Sha256Checksum, long? Size, string? WebViewLink);
