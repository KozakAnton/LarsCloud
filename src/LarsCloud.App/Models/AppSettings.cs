using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace LarsCloud.Models;

public sealed class SyncFolderSettings
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";

    [JsonIgnore]
    public string Name => GetFolderName(Path);

    public static SyncFolderSettings Create(string path)
    {
        var normalized = NormalizePath(path);
        return new SyncFolderSettings
        {
            Id = CreateStableId(normalized),
            Path = normalized
        };
    }

    public void Normalize()
    {
        Path = NormalizePath(Path);
        if (string.IsNullOrWhiteSpace(Id)) Id = CreateStableId(Path);
    }

    public static string GetFolderName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Папка";
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        var root = System.IO.Path.GetPathRoot(path)?.TrimEnd(System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar, ':');
        return string.IsNullOrWhiteSpace(root) ? "Папка" : root;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed)) return "";
        try { return System.IO.Path.GetFullPath(trimmed); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    private static string CreateStableId(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Guid.NewGuid().ToString("N");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }
}

public sealed class AppSettings
{
    public const int MaximumSyncFolders = 10;

    // Kept for a seamless upgrade from versions that supported one folder.
    public string LocalFolder { get; set; } = "";
    public List<SyncFolderSettings> SyncFolders { get; set; } = new();
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

        SyncFolders ??= new List<SyncFolderSettings>();
        if (SyncFolders.Count == 0 && !string.IsNullOrWhiteSpace(LocalFolder))
            SyncFolders.Add(SyncFolderSettings.Create(LocalFolder));

        var normalized = new List<SyncFolderSettings>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in SyncFolders.Where(x => x is not null))
        {
            folder.Normalize();
            if (string.IsNullOrWhiteSpace(folder.Path) || !paths.Add(folder.Path) || !names.Add(folder.Name)) continue;
            normalized.Add(folder);
            if (normalized.Count == MaximumSyncFolders) break;
        }

        SyncFolders = normalized;
        LocalFolder = SyncFolders.FirstOrDefault()?.Path ?? "";
    }
}
