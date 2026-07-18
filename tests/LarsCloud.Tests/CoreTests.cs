using LarsCloud.Infrastructure;
using LarsCloud.Models;

namespace LarsCloud.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1024, "1,0 КБ")]
    [InlineData(1073741824, "1,0 ГБ")]
    public void Bytes_FormatsUkrainianUnits(long bytes, string expected) =>
        Assert.Equal(expected, Formatters.Bytes(bytes));

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(99, 7)]
    public void Settings_NormalizeLimitsInterval(int input, int expected)
    {
        var settings = new AppSettings { SyncIntervalDays = input };
        settings.Normalize();
        Assert.Equal(expected, settings.SyncIntervalDays);
    }

    [Fact]
    public void Settings_NormalizeMigratesLegacyFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "LarsCloudLegacy");
        var settings = new AppSettings { LocalFolder = path };

        settings.Normalize();

        var folder = Assert.Single(settings.SyncFolders);
        Assert.Equal(Path.GetFullPath(path), folder.Path);
        Assert.Equal(folder.Path, settings.LocalFolder);
        Assert.False(string.IsNullOrWhiteSpace(folder.Id));
    }

    [Fact]
    public void Settings_NormalizeLimitsFoldersAndKeepsDriveNamesUnique()
    {
        var root = Path.Combine(Path.GetTempPath(), "LarsCloudFolders");
        var settings = new AppSettings
        {
            SyncFolders = new List<SyncFolderSettings>
            {
                SyncFolderSettings.Create(Path.Combine(root, "Folder0")),
                SyncFolderSettings.Create(Path.Combine(root, "Elsewhere", "Folder0"))
            }
        };
        settings.SyncFolders.AddRange(Enumerable.Range(1, 12)
            .Select(index => SyncFolderSettings.Create(Path.Combine(root, $"Folder{index}"))));

        settings.Normalize();

        Assert.Equal(AppSettings.MaximumSyncFolders, settings.SyncFolders.Count);
        Assert.Equal(settings.SyncFolders.Count,
            settings.SyncFolders.Select(folder => folder.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SyncFileKey_DistinguishesSameRelativePathInDifferentFolders()
    {
        Assert.NotEqual(SyncFileKey.Create("folder-a", "video.mp4"),
            SyncFileKey.Create("folder-b", "video.mp4"));
    }

    [Fact]
    public void ProductConfig_RejectsPlaceholders()
    {
        var config = new ProductConfiguration
        {
            GoogleClientId = "PASTE_YOUR_DESKTOP_OAUTH_CLIENT_ID.apps.googleusercontent.com",
            GitHubOwner = "PASTE_GITHUB_OWNER"
        };
        Assert.False(config.HasGoogleConfiguration);
        Assert.False(config.HasGitHubConfiguration);
    }
}
