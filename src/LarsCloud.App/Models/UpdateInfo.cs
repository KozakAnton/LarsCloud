namespace LarsCloud.Models;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string ReleaseNotes,
    string DownloadUrl,
    string? Sha256,
    string ReleasePageUrl);
