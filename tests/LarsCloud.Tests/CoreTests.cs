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
