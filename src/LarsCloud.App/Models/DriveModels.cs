using System.Security.Cryptography;
using System.Text;

namespace LarsCloud.Models;

public sealed record DriveQuota(long? Limit, long Usage)
{
    public long? Remaining => Limit is null ? null : Math.Max(0, Limit.Value - Usage);
    public double UsedPercent => Limit is > 0 ? Math.Clamp(Usage * 100d / Limit.Value, 0, 100) : 0;
}

public sealed record DriveAbout(string DisplayName, string Email, DriveQuota Quota);
public sealed record DriveFolder(string Id, string Name, string WebUrl);
public sealed record DriveFile(string Id, string Name, string? Md5Checksum, string? Sha256Checksum, long? Size, string? WebViewLink);

public static class DriveMetadata
{
    // Google Drive counts the UTF-8 bytes of both the key and value of each custom property.
    public const int MaximumPropertyUtf8Bytes = 124;

    public static IReadOnlyDictionary<string, string> CreateAppProperties(string syncFolderId, string relativePath)
    {
        var normalizedPath = (relativePath ?? "").Replace('\\', '/');
        var identity = $"{syncFolderId ?? ""}\n{normalizedPath}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var properties = new Dictionary<string, string>
        {
            ["lcId"] = id,
            ["lcSchema"] = "1"
        };

        if (properties.Any(property => GetUtf8Size(property.Key, property.Value) > MaximumPropertyUtf8Bytes))
            throw new InvalidOperationException("Внутрішні метадані файла перевищують ліміт Google Drive.");
        return properties;
    }

    public static int GetUtf8Size(string key, string value) =>
        Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
}
